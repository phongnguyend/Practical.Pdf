using PdfObjectKit.Application;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;
using PdfObjectKit.Editors;
using PdfObjectKit.Converters;
using PdfObjectKit.Writers;
using PdfObjectKit.Extractors;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Rendering;

const int maxBytes = 20 * 1024 * 1024;
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxBytes + 5 * 1024 * 1024 + 65536);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var app = builder.Build();
using var gate = new SemaphoreSlim(1);
PdfRedactionEndpoint.Map(app, gate);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/documents/compress", async (HttpRequest request, CancellationToken cancellation) =>
{
    if (!request.HasFormContentType)
    {
        return Results.Problem("Send a PDF file and compression options as multipart form data.", statusCode: 415);
    }
    if (!await gate.WaitAsync(0, cancellation))
    {
        return Results.Problem("Another document operation is running. Try again shortly.", statusCode: 429);
    }
    try
    {
        var form = await request.ReadFormAsync(cancellation);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length is <= 0 or > maxBytes)
        {
            return Results.Problem("Provide a PDF file up to 20 MB.", statusCode: 400);
        }
        var options = string.IsNullOrWhiteSpace(form["options"])
            ? new PdfObjectKit.Options.PdfCompressionOptions()
            : JsonSerializer.Deserialize<PdfObjectKit.Options.PdfCompressionOptions>(form["options"].ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new ArgumentException("Provide compression options.");
        // Server limits and the no-growth guarantee cannot be overridden by the client.
        options = new()
        {
            RemoveUnusedObjects = options.RemoveUnusedObjects,
            CompressStreams = options.CompressStreams,
            DeduplicateResources = options.DeduplicateResources
        };
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellation);
        var result = PdfDocumentCompressor.Compress(buffer.ToArray(), options, cancellation);
        return Results.Ok(new
        {
            base64 = Convert.ToBase64String(result.Bytes),
            result.OriginalBytes,
            outputBytes = result.Bytes.Length,
            result.OptimizedBytes,
            result.RemovedObjects,
            result.CompressedStreams,
            result.DeduplicatedResources,
            result.UsedOriginal,
            result.Notes
        });
    }
    catch (PdfObjectKit.Security.PdfPasswordException ex)
    {
        return Results.Problem(ex.Message, statusCode: 401);
    }
    catch (BadHttpRequestException ex)
    {
        return Results.Problem("The upload could not be read or exceeds the upload limit.", statusCode: ex.StatusCode);
    }
    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or FormatException or OverflowException or JsonException)
    {
        return Results.Problem($"Cannot compress this PDF: {ex.Message}", statusCode: 422);
    }
    finally
    {
        gate.Release();
    }
});
app.MapPost("/api/documents/merge", async (HttpRequest request, CancellationToken cancellation) =>
{
    const long maxMergeBytes = 64L * 1024 * 1024;
    if (!request.HasFormContentType)
    {
        return Results.Problem("Send PDF files as multipart form data.", statusCode: 415);
    }
    var sizeFeature = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (sizeFeature is { IsReadOnly: false })
    {
        sizeFeature.MaxRequestBodySize = maxMergeBytes + 65536;
    }
    if (request.ContentLength > maxMergeBytes + 65536)
    {
        return Results.Problem("Merge uploads must total at most 64 MB.", statusCode: 413);
    }
    if (!await gate.WaitAsync(0, cancellation))
    {
        return Results.Problem("Another document operation is running. Try again shortly.", statusCode: 429);
    }
    try
    {
        var form = await request.ReadFormAsync(cancellation);
        var files = form.Files.GetFiles("files");
        if (files.Count is < 2 or > 32 || files.Count != form.Files.Count)
        {
            return Results.Problem("Select between 2 and 32 PDF tabs to merge.", statusCode: 400);
        }
        if (files.Any(file => file.Length is <= 0 or > maxBytes) || files.Sum(file => file.Length) > maxMergeBytes)
        {
            return Results.Problem("Choose PDFs up to 20 MB each and 64 MB combined.", statusCode: 413);
        }
        // Parse lazily so assembly does not retain every input graph at once.
        IEnumerable<PdfObjectDocument> ReadDocuments()
        {
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                using var stream = file.OpenReadStream();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                yield return ReadWorkingPdf(buffer.ToArray());
            }
        }
        var merged = PdfDocumentMerger.Merge(ReadDocuments(), new PdfObjectKit.Options.PdfPageAssemblyOptions
        {
            MaxPages = 100,
            MaxObjects = 250000,
            MaxNodes = 1000000,
            MaxEncodedStreamBytes = maxMergeBytes,
            NamespaceFormFields = true,
            RenameConflictingAttachments = true,
            RenameConflictingDestinations = true
        }, cancellation);
        var bytes = PdfWriter.Write(merged);
        if (bytes.Length > maxBytes)
        {
            return Results.Problem("The merged PDF exceeds the playground's 20 MB document limit.", statusCode: 422);
        }
        return Results.File(bytes, "application/pdf", "merged.pdf");
    }
    catch (PdfObjectKit.Security.PdfPasswordException ex)
    {
        return Results.Problem(ex.Message, statusCode: 401);
    }
    catch (BadHttpRequestException ex)
    {
        return Results.Problem("The merge upload could not be read or exceeds the upload limit.", statusCode: ex.StatusCode);
    }
    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or FormatException or OverflowException)
    {
        return Results.Problem($"Cannot merge these PDFs: {ex.Message}", statusCode: 422);
    }
    finally
    {
        gate.Release();
    }
});

app.MapPost("/api/documents/edit", async (HttpRequest request, CancellationToken cancellation) =>
{
    if (!request.HasFormContentType)
    {
        return Results.Problem("Send multipart form data containing file and edit.", statusCode: 415);
    }

    if (!await gate.WaitAsync(0, cancellation))
    {
        return Results.Problem("Another document operation is running. Try again shortly.", statusCode: 429);
    }

    try
    {
        var form = await request.ReadFormAsync(cancellation);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0 || file.Length > maxBytes)
        {
            return Results.Problem("Provide a PDF file up to 20 MB.", statusCode: 400);
        }

        var edit = JsonSerializer.Deserialize<EditRequest>(form["edit"].ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });
        if (edit?.Value is null || edit.Value.Length > 10000)
        {
            return Results.Problem("Provide an edit value of at most 10000 characters.", statusCode: 400);
        }

        if (edit.Values is not null && (edit.Values.Count > 100 || edit.Values.Any(value => value is null || value.Length > 10000)))
        {
            return Results.Problem("Provide at most 100 selection values, each at most 10000 characters.", statusCode: 400);
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellation);
        var document = ReadWorkingPdf(buffer.ToArray());
        var bytes = await DocumentEdits.Apply(document, edit, async (name, limit) =>
        {
            var asset = form.Files.GetFile(name);
            if (asset is null || asset.Length <= 0 || asset.Length > limit)
            {
                throw new ArgumentException($"Provide {name} up to {limit} bytes.");
            }
            using var assetBuffer = new MemoryStream();
            await asset.CopyToAsync(assetBuffer, cancellation);
            return assetBuffer.ToArray();
        }, cancellation);
        if (bytes.Length > maxBytes)
        {
            return Results.Problem("The edited PDF exceeds the playground's 20 MB limit.", statusCode: 422);
        }

        return Results.File(bytes, "application/pdf", "edited.pdf");
    }
    catch (PdfObjectKit.Security.PdfPasswordException ex)
    {
        return Results.Problem(ex.Message, statusCode: 401);
    }
    catch (BadHttpRequestException ex)
    {
        return Results.Problem("The edit request could not be read.", statusCode: ex.StatusCode);
    }
    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or JsonException or FormatException or OverflowException or CryptographicException or System.Xml.XmlException)
    {
        return Results.Problem($"Edit rejected: {ex.Message}", statusCode: 422);
    }
    finally
    {
        gate.Release();
    }
});
Func<HttpRequest, CancellationToken, Task<IResult>> inspectDocument = async (request, cancellation) =>
{
    if (!string.Equals(request.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Problem("Send the PDF bytes with Content-Type: application/pdf.", statusCode: 415);
    }

    if (request.ContentLength > maxBytes)
    {
        return Results.Problem("The upload limit is 20 MB.", statusCode: 413);
    }

    if (!await gate.WaitAsync(0, cancellation))
    {
        return Results.Problem("Another document is being inspected. Try again shortly.", statusCode: 429);
    }

    try
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellation)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return Results.Problem("The upload limit is 20 MB.", statusCode: 413);
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            return Results.Problem("The uploaded file does not start with a PDF header.", statusCode: 400);
        }

        var document = PdfReader.Read(bytes);
        if (document.Security is not null)
        {
            return Results.Problem("Unlock this encrypted PDF before opening it in the playground.", statusCode: 401);
        }
        if (request.Path == "/api/documents/search")
        {
            return Results.Ok(PdfTextSearcher.Search(document, request.Query["query"].ToString(), new()
            {
                MatchCase = request.Query["matchCase"] == "true",
                WholeWord = request.Query["wholeWord"] == "true",
                MaxResults = 500
            }, cancellation));
        }
        if (request.Path == "/api/documents/flatten")
        {
            var flattened = PdfFlattenEditor.Flatten(document, new()
            {
                FormFields = request.Query["forms"] != "false",
                Annotations = request.Query["annotations"] != "false"
            }, cancellation);
            var output = PdfWriter.Write(flattened);
            if (output.Length > maxBytes)
            {
                return Results.Problem("The flattened PDF exceeds the playground's 20 MB document limit.", statusCode: 422);
            }
            return Results.File(output, "application/pdf", "flattened.pdf");
        }
        if (request.Path == "/api/documents/split" || request.Path == "/api/documents/extract")
        {
            var options = new PdfObjectKit.Options.PdfPageAssemblyOptions
            {
                MaxPages = 100,
                MaxObjects = 250000,
                MaxNodes = 1000000,
                MaxEncodedStreamBytes = 64L * 1024 * 1024
            };
            IEnumerable<PdfObjectKit.Models.PdfObjectDocument> outputs;
            if (request.Path == "/api/documents/extract")
            {
                var text = request.Query["pages"].ToString();
                var parts = text.Split(',');
                if (parts.Length > 100 || parts.Any(part => !int.TryParse(part, out var number) || number < 1))
                {
                    return Results.Problem("Provide up to 100 positive page numbers separated by commas.", statusCode: 400);
                }
                outputs = [PdfPageEditor.ExtractPages(document, parts.Select(int.Parse).ToArray(), options, cancellation)];
            }
            else
            {
                outputs = PdfPageEditor.SplitPages(document, options, cancellation);
            }
            var results = new List<object>();
            long total = 0;
            foreach (var output in outputs)
            {
                cancellation.ThrowIfCancellationRequested();
                if (results.Count == 100)
                {
                    return Results.Problem("The playground can split at most 100 pages at once.", statusCode: 422);
                }
                var outputBytes = PdfWriter.Write(output);
                total += outputBytes.Length;
                if (outputBytes.Length > maxBytes || total > 64L * 1024 * 1024)
                {
                    return Results.Problem("Split/extract output exceeds 20 MB per document or 64 MB total.", statusCode: 422);
                }
                results.Add(new { pageNumber = results.Count + 1, base64 = Convert.ToBase64String(outputBytes) });
            }
            return Results.Ok(new { documents = results });
        }
        if (request.Path == "/api/documents/image")
        {
            if (!int.TryParse(request.Query["page"], out var imagePage) || !int.TryParse(request.Query["image"], out var imageNumber))
            {
                return Results.Problem("Provide page and image numbers.", statusCode: 400);
            }
            return Results.Ok(PdfCanvasExporter.ExportImage(document, imagePage, imageNumber));
        }
        if (request.Path == "/api/documents/render")
        {
            if (!int.TryParse(request.Query["page"], out var pageNumber))
            {
                return Results.Problem("Provide a page number.", statusCode: 400);
            }
            return Results.Ok(PdfCanvasExporter.Export(document, pageNumber));
        }
        var images = PdfImageExtractor.Extract(document);
        if (images.Count > 100)
        {
            return Results.Problem("The playground supports up to 100 pages.", statusCode: 422);
        }

        if (request.Path == "/api/documents/markdown")
        {
            var json = request.Query["options"].ToString();
            if (json.Length > 16384)
            {
                return Results.Problem("Markdown options exceed the size limit.", statusCode: 400);
            }
            var options = string.IsNullOrEmpty(json) ? new MarkdownConversionOptions()
                : JsonSerializer.Deserialize<MarkdownConversionOptions>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                }) ?? throw new ArgumentException("Provide a Markdown options object.");
            var result = PdfMarkdownConverter.Convert(document, options.ToConverterOptions());
            return Results.Ok(new { result.Markdown, result.Warnings, options, defaultOptions = new MarkdownConversionOptions() });
        }

        return Results.Ok(DocumentInspection.Inspect(document, cancellation));
    }
    catch (PdfObjectKit.Security.PdfPasswordException ex)
    {
        return Results.Problem(ex.Message, statusCode: 401);
    }
    catch (BadHttpRequestException ex)
    {
        return Results.Problem("The upload could not be read or exceeds the upload limit.", statusCode: ex.StatusCode);
    }
    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FormatException or ArgumentException or OverflowException or CryptographicException or JsonException)
    {
        var operation = request.Path == "/api/documents/markdown" ? "convert" : request.Path == "/api/documents/flatten" ? "flatten" : request.Path == "/api/documents/split" ? "split" : request.Path == "/api/documents/extract" ? "extract pages from" : "inspect";
        return Results.Problem($"Cannot {operation} this PDF: {ex.Message}", statusCode: 422);
    }
    finally
    {
        gate.Release();
    }
};
app.MapPost("/api/documents/inspect", inspectDocument);
app.MapPost("/api/documents/split", inspectDocument);
app.MapPost("/api/documents/extract", inspectDocument);
app.MapPost("/api/documents/markdown", inspectDocument);
app.MapPost("/api/documents/render", inspectDocument);
app.MapPost("/api/documents/image", inspectDocument);
app.MapPost("/api/documents/search", inspectDocument);
app.MapPost("/api/documents/flatten", inspectDocument);

app.MapPost("/api/documents/security/{operation}", async (string operation, HttpRequest request, CancellationToken cancellation) =>
{
    if (operation is not ("unlock" or "encrypt") || !request.HasFormContentType)
    {
        return Results.Problem("Choose unlock or encrypt with a multipart PDF upload.", statusCode: 400);
    }
    if (!await gate.WaitAsync(0, cancellation))
    {
        return Results.Problem("Another document operation is in progress.", statusCode: 429);
    }
    try
    {
        var form = await request.ReadFormAsync(cancellation);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0 || file.Length > maxBytes)
        {
            return Results.Problem("Provide a PDF up to 20 MB.", statusCode: 400);
        }
        var password = form["password"].ToString();
        if (password.Length > 4096 || form["options"].ToString().Length > 16384)
        {
            return Results.Problem("Security settings exceed the allowed length.", statusCode: 400);
        }
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellation);
        var document = PdfReader.Read(buffer.ToArray(), new() { Password = password });
        if (PdfFormExtractor.Extract(document).Any(field => field.IsSigned))
        {
            throw new NotSupportedException("Changing security would invalidate existing digital signatures.");
        }
        if (document.Security is { OwnerAuthenticated: false } security
            && (!security.AllowModifying || !security.AllowCopying || !security.AllowDocumentAssembly || !security.AllowAnnotations || !security.AllowFormFilling || !security.AllowHighQualityPrinting))
        {
            return Results.Problem("Enter the owner password to open a restricted PDF for editing.", statusCode: 403);
        }
        PdfObjectKit.Options.PdfWriteOptions? settings = null;
        if (operation == "encrypt")
        {
            var encryption = JsonSerializer.Deserialize<PdfObjectKit.Options.PdfEncryptionOptions>(form["options"].ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (encryption is null)
            {
                return Results.Problem("Provide encryption settings.", statusCode: 400);
            }
            settings = new() { Encryption = encryption };
        }
        var output = PdfWriter.Write(document, settings);
        if (output.Length > maxBytes)
        {
            return Results.Problem("The resulting PDF exceeds 20 MB.", statusCode: 422);
        }
        return Results.File(output, "application/pdf", operation == "encrypt" ? "protected.pdf" : "unlocked.pdf");
    }
    catch (PdfObjectKit.Security.PdfPasswordException ex)
    {
        return Results.Problem(ex.Message, statusCode: 401);
    }
    catch (BadHttpRequestException ex)
    {
        return Results.Problem("The upload could not be read.", statusCode: ex.StatusCode);
    }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException or JsonException or CryptographicException or FormatException or OverflowException)
    {
        return Results.Problem(ex.Message, statusCode: 422);
    }
    finally
    {
        gate.Release();
    }
});

app.Run();

static PdfObjectDocument ReadWorkingPdf(byte[] bytes)
{
    var document = PdfReader.Read(bytes);
    if (document.Security is not null)
    {
        throw new NotSupportedException("Unlock this encrypted PDF before editing or assembling it.");
    }
    return document;
}
