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

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
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
        var document = PdfReader.Read(buffer.ToArray());
        if (edit.Kind == "delete-text-line" && edit.Runs is { Count: > 0 })
        {
            PdfTextEditor.DeleteText(document, edit.PageNumber, edit.Runs);
        }
        else if (edit.Kind == "move-text" && edit.Runs is { Count: > 0 })
        {
            PdfTextEditor.MoveText(document, edit.PageNumber, edit.Runs, edit.DeltaX, edit.DeltaY);
        }
        else if (edit.Kind == "move-image")
        {
            PdfImageEditor.MoveImage(document, edit.PageNumber, edit.ImageNumber, edit.DeltaX, edit.DeltaY);
        }
        else if (edit.Kind == "move-field" && edit.FieldName is not null)
        {
            PdfFormEditor.MoveWidget(document, edit.FieldName, edit.WidgetIndex, edit.DeltaX, edit.DeltaY);
        }
        else if (edit.Kind == "add-text")
        {
            PdfPageEditor.InsertText(document, edit.PageNumber, edit.Value, edit.X, edit.Y, edit.FontSize);
        }
        else if (edit.Kind is "add-image" or "replace-image")
        {
            var image = form.Files.GetFile("image");
            if (image is null || image.Length > 5 * 1024 * 1024)
            {
                return Results.Problem("Choose a PNG or JPEG image up to 5 MB.", statusCode: 400);
            }

            using var imageBuffer = new MemoryStream();
            await image.CopyToAsync(imageBuffer, cancellation);
            if (edit.Kind == "replace-image")
            {
                PdfImageEditor.ReplaceImage(document, edit.PageNumber, edit.ImageNumber, imageBuffer.ToArray(), edit.ImageFit);
            }
            else
            {
                PdfImageEditor.InsertImage(document, edit.PageNumber, imageBuffer.ToArray(), edit.X, edit.Y, edit.Width, edit.Height);
                PdfPageEditor.ConsolidateContent(document, edit.PageNumber);
            }
        }
        else if (edit.Kind == "delete-image")
        {
            PdfPageEditor.DeleteImage(document, edit.PageNumber, edit.ImageNumber);
        }
        else if (edit.Kind == "add-field" && edit.FieldName is not null)
        {
            switch (edit.FieldType)
            {
                case PdfFormFieldKind.Text:
                    PdfFormEditor.InsertTextField(document, edit.PageNumber, edit.FieldName, edit.Value, edit.X, edit.Y, edit.Width, edit.Height, edit.TextAlignment ?? PdfTextAlignment.Left, edit.Multiline);
                    break;
                case PdfFormFieldKind.PushButton:
                    PdfFormEditor.InsertPushButton(document, edit.PageNumber, edit.FieldName, edit.Value, edit.X, edit.Y, edit.Width, edit.Height);
                    break;
                case PdfFormFieldKind.Signature:
                    PdfFormEditor.InsertSignatureField(document, edit.PageNumber, edit.FieldName, edit.X, edit.Y, edit.Width, edit.Height);
                    break;
                case PdfFormFieldKind.CheckBox:
                    if (edit.Value is not ("Yes" or "Off"))
                    {
                        throw new ArgumentException("Checkbox value must be Yes or Off.");
                    }
                    PdfFormEditor.InsertCheckBox(document, edit.PageNumber, edit.FieldName, edit.Value == "Yes", edit.X, edit.Y, edit.Width, edit.Height);
                    break;
                case PdfFormFieldKind.Choice:
                    if (edit.MultiSelect)
                    {
                        PdfFormEditor.InsertMultiSelectListBox(document, edit.PageNumber, edit.FieldName, edit.Choices ?? [], edit.Values ?? [], edit.X, edit.Y, edit.Width, edit.Height, edit.TextAlignment ?? PdfTextAlignment.Left);
                    }
                    else if (edit.ListBox)
                    {
                        PdfFormEditor.InsertListBox(document, edit.PageNumber, edit.FieldName, edit.Choices ?? [], edit.Value, edit.X, edit.Y, edit.Width, edit.Height, edit.TextAlignment ?? PdfTextAlignment.Left);
                    }
                    else
                    {
                        PdfFormEditor.InsertDropdown(document, edit.PageNumber, edit.FieldName, edit.Choices ?? [], edit.Value, edit.X, edit.Y, edit.Width, edit.Height, edit.TextAlignment ?? PdfTextAlignment.Left);
                    }
                    break;
                case PdfFormFieldKind.RadioButton:
                    PdfFormEditor.InsertRadioGroup(document, edit.PageNumber, edit.FieldName, edit.Choices ?? [], edit.Value, edit.X, edit.Y, edit.Width, edit.Height);
                    break;
                default:
                    throw new ArgumentException("Choose a supported form field type.");
            }
        }
        else if (edit.Kind == "button-caption" && edit.FieldName is not null)
        {
            PdfFormEditor.SetPushButtonCaption(document, edit.FieldName, edit.Value);
        }
        else if (edit.Kind == "rename-field" && edit.FieldName is not null)
        {
            PdfFormEditor.RenameField(document, edit.FieldName, edit.Value);
        }
        else if (edit.Kind == "delete-field" && edit.FieldName is not null)
        {
            PdfFormEditor.DeleteField(document, edit.FieldName);
        }
        else if (edit.Kind == "text" && edit.Run is not null)
        {
            PdfTextEditor.UpdateText(document, edit.PageNumber, edit.Run, edit.Value);
        }
        else if (edit.Kind == "field" && edit.FieldName is not null)
        {
            if (edit.Values is not null)
            {
                PdfFormEditor.SetValues(document, edit.FieldName, edit.Values, edit.TextAlignment);
            }
            else
            {
                PdfFormEditor.SetValue(document, edit.FieldName, edit.Value, edit.TextAlignment);
            }
        }
        else
        {
            return Results.Problem("Choose a text run or form field to edit.", statusCode: 400);
        }

        var bytes = PdfWriter.Write(document);
        if (bytes.Length > maxBytes)
        {
            return Results.Problem("The edited PDF exceeds the playground's 20 MB limit.", statusCode: 422);
        }

        return Results.File(bytes, "application/pdf", "edited.pdf");
    }
    catch (BadHttpRequestException ex)
    {
        return Results.Problem("The edit request could not be read.", statusCode: ex.StatusCode);
    }
    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or JsonException or FormatException or OverflowException)
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
            var result = PdfMarkdownConverter.Convert(document);
            return Results.Ok(new { result.Markdown, result.Warnings });
        }

        var warnings = new List<string>();
        var items = new List<CanvasObject>();
        foreach (var page in images)
        {
            cancellation.ThrowIfCancellationRequested();
            if (page.Warning is not null)
            {
                warnings.Add($"Page {page.PageNumber}: {page.Warning}");
            }

            foreach (var image in page.Images)
            {
                items.Add(new($"p{page.PageNumber}-image{image.ImageNumber}", page.PageNumber, "image", $"Image {image.ImageNumber}", image.Corners, image));
            }

            try
            {
                var lineNumber = 0;
                foreach (var line in PdfParagraphExtractor.Extract(document, page.PageNumber).SelectMany(p => p.Lines))
                {
                    items.Add(new($"p{page.PageNumber}-text{++lineNumber}", page.PageNumber, "text", line.Text,
                        Rectangle(line.Left, line.Baseline - line.Height * .2, line.Right, line.Baseline + line.Height), line));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                warnings.Add($"Page {page.PageNumber}: text overlays unavailable. {ex.Message}");
            }
        }

        try
        {
            var widgetNumber = 0;
            foreach (var field in PdfFormExtractor.Extract(document))
            {
                foreach (var widget in field.Widgets)
                {
                    if (widget.PageNumber is { } page && widget.Rectangle is { Count: 4 } r)
                    {
                        items.Add(new($"widget{++widgetNumber}", page, "field", field.Name, Rectangle(r[0], r[1], r[2], r[3]), new { field.Name, field.Kind, field.Values, field.TextAlignment, field.IsReadOnly, field.Flags, field.Choices, widgetIndex = field.Widgets.IndexOf(widget), states = field.Widgets.SelectMany(w => w.AppearanceStates).Distinct(), widget }));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            warnings.Add($"Form overlays unavailable. {ex.Message}");
        }

        return Results.Ok(new { document.PdfVersion, pageCount = images.Count, objectCount = document.Objects.Count, objects = items, warnings });
    }
    catch (BadHttpRequestException ex)
    {
        return Results.Problem("The upload could not be read or exceeds the upload limit.", statusCode: ex.StatusCode);
    }
    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FormatException or ArgumentException or OverflowException)
    {
        return Results.Problem($"Cannot inspect this PDF: {ex.Message}", statusCode: 422);
    }
    finally
    {
        gate.Release();
    }
};
app.MapPost("/api/documents/inspect", inspectDocument);
app.MapPost("/api/documents/markdown", inspectDocument);
app.MapPost("/api/documents/render", inspectDocument);
app.MapPost("/api/documents/image", inspectDocument);

app.Run();

static List<PdfImagePoint> Rectangle(double left, double bottom, double right, double top)
{
    return [new(left, bottom), new(right, bottom), new(right, top), new(left, top)];
}

internal sealed record CanvasObject(string Id, int PageNumber, string Kind, string Label, List<PdfImagePoint> Corners, object Details);

internal sealed record EditRequest(string Kind, string? Value, int PageNumber, PdfTextRun? Run, string? FieldName,
    double X = 40, double Y = 40, double Width = 150, double Height = 50, double FontSize = 12, int ImageNumber = 0, PdfTextAlignment? TextAlignment = null, PdfImageFit ImageFit = PdfImageFit.Fit, int WidgetIndex = 0, double DeltaX = 0, double DeltaY = 0, List<PdfTextRun>? Runs = null, PdfFormFieldKind FieldType = PdfFormFieldKind.Text, List<PdfFormChoice>? Choices = null, bool Multiline = false, bool ListBox = false, bool MultiSelect = false, List<string>? Values = null);
