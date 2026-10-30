using PdfObjectKit.Readers;
using System.Text.Json;
using PdfObjectKit.Editors;
using PdfObjectKit.Models;
using PdfObjectKit.Writers;

internal static class PdfRedactionEndpoint
{
    internal sealed record PageSpec(int PixelWidth, int PixelHeight, double WidthPoints, double HeightPoints, List<PdfRasterRedaction> Regions);

    public static void Map(WebApplication app, SemaphoreSlim gate)
    {
        async Task<IResult> Content(HttpRequest request, CancellationToken cancellation, bool preview)
        {
            if (!request.HasFormContentType)
            {
                return Results.Problem("Provide a PDF and redaction rectangles.", statusCode: 415);
            }
            if (!await gate.WaitAsync(0, cancellation))
            {
                return Results.Problem("Another document operation is running. Try again shortly.", statusCode: 429);
            }
            try
            {
                var form = await request.ReadFormAsync(cancellation);
                var file = form.Files.GetFile("file");
                var json = form["regions"].ToString();
                if (file is null || file.Length is <= 0 or > 20 * 1024 * 1024 || json.Length > 256 * 1024)
                {
                    return Results.Problem("Provide a PDF up to 20 MB and at most 1000 redaction rectangles.", statusCode: 400);
                }
                var regions = JsonSerializer.Deserialize<List<PdfContentRedaction>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new ArgumentException("Provide redaction rectangles.");
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, cancellation);
                var document = PdfReader.Read(buffer.ToArray());
                if (preview)
                {
                    return Results.Ok(PdfContentRedactor.Preview(document, regions, cancellation));
                }
                var result = PdfContentRedactor.Apply(document, regions, cancellation);
                var bytes = PdfWriter.Write(result);
                if (bytes.Length > 20 * 1024 * 1024)
                {
                    return Results.Problem("The redacted PDF exceeds the 20 MB output limit.", statusCode: 422);
                }
                return Results.File(bytes, "application/pdf", "redacted.pdf");
            }
            catch (BadHttpRequestException ex)
            {
                return Results.Problem("Invalid or excessive PDF upload.", statusCode: ex.StatusCode);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or JsonException or OverflowException or FormatException)
            {
                return Results.Problem("Content redaction rejected: " + ex.Message, statusCode: 422);
            }
            finally
            {
                gate.Release();
            }
        }
        app.MapPost("/api/documents/redact-content", (HttpRequest request, CancellationToken cancellation) => Content(request, cancellation, false));
        app.MapPost("/api/documents/redact-content/preview", (HttpRequest request, CancellationToken cancellation) => Content(request, cancellation, true));
        app.MapPost("/api/documents/redact-raster", async (HttpRequest request, CancellationToken cancellation) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.Problem("Provide rendered RGB pages and a manifest.", statusCode: 415);
            }
            var limit = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false })
            {
                limit.MaxRequestBodySize = PdfRasterRedactor.MaxRasterBytes + 1024 * 1024;
            }
            if (!await gate.WaitAsync(0, cancellation))
            {
                return Results.Problem("Another document operation is running. Try again shortly.", statusCode: 429);
            }
            try
            {
                var form = await request.ReadFormAsync(cancellation);
                var json = form["manifest"].ToString();
                if (json.Length > 512 * 1024)
                {
                    return Results.Problem("Too many redaction marks.", statusCode: 400);
                }
                var specs = JsonSerializer.Deserialize<List<PageSpec>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (specs is null || specs.Count is < 1 or > 100 || form.Files.Count != specs.Count
                    || form.Files.Sum(file => file.Length) > PdfRasterRedactor.MaxRasterBytes)
                {
                    return Results.Problem("Provide 1-100 raster pages, totaling at most 64 MB.", statusCode: 400);
                }
                var pages = new List<PdfRedactionRasterPage>();
                for (var i = 0; i < specs.Count; i++)
                {
                    var spec = specs[i];
                    var file = form.Files[i];
                    if (spec is null || spec.PixelWidth <= 0 || spec.PixelHeight <= 0
                        || (long)spec.PixelWidth * spec.PixelHeight > 16000000
                        || file.Length != (long)spec.PixelWidth * spec.PixelHeight * 3)
                    {
                        return Results.Problem("Invalid raster dimensions or byte count.", statusCode: 400);
                    }
                    using var buffer = new MemoryStream();
                    await file.CopyToAsync(buffer, cancellation);
                    pages.Add(new(spec.PixelWidth, spec.PixelHeight, spec.WidthPoints, spec.HeightPoints, buffer.ToArray(), spec.Regions));
                }
                var output = PdfWriter.Write(PdfRasterRedactor.CreateDocument(pages, cancellation));
                if (output.Length > 20 * 1024 * 1024)
                {
                    return Results.Problem("The result exceeds 20 MB. Choose a lower resolution.", statusCode: 422);
                }
                return Results.File(output, "application/pdf", "redacted.pdf");
            }
            catch (BadHttpRequestException ex)
            {
                return Results.Problem("Raster upload exceeds the limit or is malformed.", statusCode: ex.StatusCode);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidDataException or OverflowException)
            {
                return Results.Problem("Cannot create redacted PDF: " + ex.Message, statusCode: 422);
            }
            finally
            {
                gate.Release();
            }
        });
    }
}
