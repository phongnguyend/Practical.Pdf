using PdfObjectKit.Models;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;

namespace PdfObjectKit.Application;

public static class DocumentInspection
{
    public static object Inspect(PdfObjectDocument document, CancellationToken cancellation = default)
    {
        var images = PdfImageExtractor.Extract(document);
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
                foreach (var line in PdfParagraphExtractor.ExtractSelectionLines(document, page.PageNumber))
                {
                    items.Add(new($"p{page.PageNumber}-text{++lineNumber}", page.PageNumber, "text", line.Text,
                        line.Corners.ToList(), line));
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
        return new { document.PdfVersion, pageCount = images.Count, objectCount = document.Objects.Count, objects = items, warnings, watermarks = PdfWatermarkEditor.GetWatermarks(document), metadata = PdfMetadataExtractor.Extract(document) };
    }

    private static List<PdfImagePoint> Rectangle(double left, double bottom, double right, double top)
    {
        return [new(left, bottom), new(right, bottom), new(right, top), new(left, top)];
    }
}

public sealed record CanvasObject(string Id, int PageNumber, string Kind, string Label, List<PdfImagePoint> Corners, object Details);
