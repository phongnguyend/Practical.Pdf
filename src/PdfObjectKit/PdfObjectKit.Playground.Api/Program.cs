using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfObjectKit.Signing;
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
        if (PdfFormExtractor.Extract(document).Any(field => field.IsSigned))
        {
            throw new NotSupportedException("This PDF contains a signature. Editing would invalidate it; signed-document editing is disabled.");
        }
        byte[]? signedBytes = null;
        if (edit.Kind == "metadata" && edit.Metadata is not null)
        {
            PdfMetadataEditor.Update(document, edit.Metadata);
        }
        else if (edit.Kind == "add-watermark" && edit.Watermark is not null)
        {
            PdfWatermarkEditor.AddText(document, edit.Watermark);
        }
        else if (edit.Kind == "update-watermark" && edit.Watermark is not null && edit.WatermarkId is not null)
        {
            PdfWatermarkEditor.UpdateText(document, edit.WatermarkId, edit.Watermark);
        }
        else if (edit.Kind == "remove-watermark" && edit.WatermarkId is not null)
        {
            PdfWatermarkEditor.Remove(document, edit.WatermarkId);
        }
        else if (edit.Kind == "rotate-page")
        {
            PdfPageEditor.RotatePage(document, edit.PageNumber, edit.Rotation);
        }
        else if (edit.Kind is "delete-page" or "duplicate-page")
        {
            var assemblyOptions = new PdfObjectKit.Options.PdfPageAssemblyOptions
            {
                MaxPages = 100,
                MaxObjects = 250000,
                MaxNodes = 1000000,
                MaxEncodedStreamBytes = 64L * 1024 * 1024
            };
            document = edit.Kind == "duplicate-page"
                ? PdfPageEditor.DuplicatePage(document, edit.PageNumber, assemblyOptions, cancellation)
                : PdfPageEditor.DeletePage(document, edit.PageNumber, assemblyOptions, cancellation);
        }
        else if (edit.Kind == "delete-objects")
        {
            if (edit.Deletions is not { Count: > 0 and <= 1000 } deletions
                || deletions.Any(item => item.PageNumber != edit.PageNumber
                    || item.Kind is not ("delete-text-line" or "delete-image" or "delete-field" or "delete-annotation")
                    || (item.Kind == "delete-text-line" && item.Runs is not { Count: > 0 })
                    || (item.Kind == "delete-annotation" && item.AnnotationNumber <= 0)
                    || (item.Kind == "delete-image" && item.ImageNumber <= 0)
                    || (item.Kind == "delete-field" && string.IsNullOrEmpty(item.FieldName))))
            {
                throw new ArgumentException("Choose between 1 and 1000 valid objects on the same page to delete.");
            }

            // One text rewrite validates every source hash against the original streams.
            // Text removal retains image invocation order; remove images from last to first.
            // The request owns this document: any failure discards every change.
            var runs = deletions.Where(item => item.Kind == "delete-text-line").SelectMany(item => item.Runs!)
                .DistinctBy(run => (run.StreamObject, run.FormOccurrenceId, run.OperatorIndex, run.OperandIndex)).ToList();
            if (runs.Count > 0)
            {
                PdfTextEditor.DeleteText(document, edit.PageNumber, runs);
            }
            foreach (var number in deletions.Where(item => item.Kind == "delete-image").Select(item => item.ImageNumber).Distinct().OrderDescending())
            {
                PdfPageEditor.DeleteImage(document, edit.PageNumber, number);
            }
            foreach (var number in deletions.Where(item => item.Kind == "delete-annotation").Select(item => item.AnnotationNumber).Distinct().OrderDescending())
            {
                PdfAnnotationEditor.Delete(document, edit.PageNumber, number);
            }
            foreach (var fieldName in deletions.Where(item => item.Kind == "delete-field").Select(item => item.FieldName!).Distinct())
            {
                PdfFormEditor.DeleteField(document, fieldName);
            }
        }
        else if (edit.Kind is "add-comment" or "add-highlight" or "add-underline" or "add-strikeout")
        {
            var kind = edit.Kind switch
            {
                "add-comment" => PdfAnnotationKind.Comment,
                "add-highlight" => PdfAnnotationKind.Highlight,
                "add-underline" => PdfAnnotationKind.Underline,
                _ => PdfAnnotationKind.StrikeOut
            };
            PdfAnnotationEditor.Add(document, edit.PageNumber, kind, edit.Regions ?? [new(edit.X, edit.Y, edit.Width, edit.Height)], edit.Value);
        }
        else if (edit.Kind == "annotation")
        {
            PdfAnnotationEditor.UpdateContents(document, edit.PageNumber, edit.AnnotationNumber, edit.Value);
        }
        else if (edit.Kind == "delete-annotation")
        {
            PdfAnnotationEditor.Delete(document, edit.PageNumber, edit.AnnotationNumber);
        }
        else if (edit.Kind == "sign-field" && edit.FieldName is not null)
        {
            var certificateFile = form.Files.GetFile("certificate");
            if (certificateFile is null || certificateFile.Length is <= 0 or > 1048576)
            {
                throw new ArgumentException("Choose a PFX/P12 certificate up to 1 MB.");
            }
            using var certificateBuffer = new MemoryStream();
            await certificateFile.CopyToAsync(certificateBuffer, cancellation);
            var certificateBytes = certificateBuffer.ToArray();
            try
            {
                using var certificate = X509CertificateLoader.LoadPkcs12(certificateBytes, edit.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
                signedBytes = PdfSigner.Sign(document, edit.FieldName, certificate, edit.Value);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(certificateBytes);
            }
        }
        else if (edit.Kind == "button-action" && edit.FieldName is not null)
        {
            PdfFormEditor.SetButtonAction(document, edit.FieldName, edit.Action);
        }
        else if (edit.Kind == "rich-text" && edit.FieldName is not null)
        {
            PdfFormEditor.SetRichText(document, edit.FieldName, edit.Value, edit.TextAlignment);
        }
        else if (edit.Kind == "delete-text-line" && edit.Runs is { Count: > 0 })
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
                    if (edit.RichText)
                    {
                        PdfFormEditor.InsertRichTextField(document, edit.PageNumber, edit.FieldName, edit.Value, edit.X, edit.Y, edit.Width, edit.Height, edit.TextAlignment ?? PdfTextAlignment.Left);
                    }
                    else
                    {
                        PdfFormEditor.InsertTextField(document, edit.PageNumber, edit.FieldName, edit.Value, edit.X, edit.Y, edit.Width, edit.Height, edit.TextAlignment ?? PdfTextAlignment.Left, edit.Multiline, edit.MaxLength, edit.Password, edit.Comb, edit.FileSelect);
                    }
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

        var bytes = signedBytes ?? PdfWriter.Write(document);
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
                        items.Add(new($"widget{++widgetNumber}", page, "field", field.Name, Rectangle(r[0], r[1], r[2], r[3]), new { field.Name, field.Kind, field.Values, field.TextAlignment, field.IsReadOnly, field.Flags, field.MaxLength, field.RichText, field.IsSigned, field.Choices, widgetIndex = field.Widgets.IndexOf(widget), states = field.Widgets.SelectMany(w => w.AppearanceStates).Distinct(), widget }));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            warnings.Add($"Form overlays unavailable. {ex.Message}");
        }

        foreach (var annotation in PdfAnnotationExtractor.Extract(document))
        {
            if (annotation.Rectangle is { Count: 4 } r)
            {
                items.Add(new($"p{annotation.PageNumber}-annotation{annotation.AnnotationNumber}", annotation.PageNumber, "annotation",
                    $"{annotation.Kind}: {annotation.Contents}", Rectangle(r[0], r[1], r[2], r[3]), annotation));
            }
        }
        return Results.Ok(new { document.PdfVersion, pageCount = images.Count, objectCount = document.Objects.Count, objects = items, warnings, watermarks = PdfWatermarkEditor.GetWatermarks(document), metadata = PdfMetadataExtractor.Extract(document) });
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
        var operation = request.Path == "/api/documents/split" ? "split" : request.Path == "/api/documents/extract" ? "extract pages from" : "inspect";
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

static List<PdfImagePoint> Rectangle(double left, double bottom, double right, double top)
{
    return [new(left, bottom), new(right, bottom), new(right, top), new(left, top)];
}

internal sealed record CanvasObject(string Id, int PageNumber, string Kind, string Label, List<PdfImagePoint> Corners, object Details);

internal sealed record EditRequest(string Kind, string? Value, int PageNumber, PdfTextRun? Run, string? FieldName,
    double X = 40, double Y = 40, double Width = 150, double Height = 50, double FontSize = 12, int ImageNumber = 0, PdfTextAlignment? TextAlignment = null, PdfImageFit ImageFit = PdfImageFit.Fit, int WidgetIndex = 0, double DeltaX = 0, double DeltaY = 0, List<PdfTextRun>? Runs = null, PdfFormFieldKind FieldType = PdfFormFieldKind.Text, List<PdfFormChoice>? Choices = null, bool Multiline = false, bool ListBox = false, bool MultiSelect = false, List<string>? Values = null, int? MaxLength = null, bool Password = false, bool Comb = false, bool FileSelect = false, bool RichText = false, PdfButtonAction? Action = null, string? CertificatePassword = null, List<EditRequest>? Deletions = null, int AnnotationNumber = 0, List<PdfAnnotationRegion>? Regions = null, int Rotation = 0, PdfObjectKit.Options.PdfTextWatermarkOptions? Watermark = null, string? WatermarkId = null, PdfMetadataUpdate? Metadata = null);
