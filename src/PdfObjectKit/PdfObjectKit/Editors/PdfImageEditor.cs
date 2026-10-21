using PdfObjectKit.Graphs;
using PdfObjectKit.Primitives;
using PdfObjectKit.Validators;
using PdfObjectKit.Writers;
using PdfObjectKit.Models;
using PdfObjectKit.Extractors;
using PdfObjectKit.Parsers;
using System.Text;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

public static class PdfImageEditor
{
    /// <summary>
    /// Inserts a JPEG or PNG over a page's existing content. Page numbers are one-based;
    /// coordinates and display dimensions are in unrotated PDF user space.
    /// The document changes only on success. Rebuild external indexes after insertion.
    /// Returns the new image object's reference. Existing references into the model must be reacquired.
    /// </summary>
    public static PdfReference InsertImage(PdfObjectDocument document, int pageNumber, byte[] imageBytes,
        double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0 || !double.IsFinite(x + width) || !double.IsFinite(y + height))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Placement must be finite with positive width and height.");
        }

        PdfDocumentValidator.Validate(document);
        var originalPages = new PdfGraph(document).Pages();
        if (pageNumber < 1 || pageNumber > originalPages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        var image = PdfImageData.Read(imageBytes);
        // Copy the object graph without changing the caller. All subsequent work
        // happens on this independent graph, including operations that may fail.
        var working = PdfNativeParser.Parse(PdfWriter.Write(document));
        var graph = new PdfGraph(working);
        var page = graph.Pages()[pageNumber - 1];
        ValidateContent(page);
        var resources = new Dictionary<string, PdfValue>(page.Resources.Entries
            ?? throw new InvalidDataException("Page resources must be a dictionary."));
        var existing = graph.Get(page.Resources, "XObject");
        if (existing is not null && existing.Type != "dictionary")
        {
            throw new InvalidDataException("XObject resources must be a dictionary.");
        }

        var xobjects = existing is null ? new Dictionary<string, PdfValue>() : new(existing.Entries!);
        var name = "PdfObjectKitImage";
        for (var suffix = 1; xobjects.ContainsKey(name); suffix++)
        {
            name = "PdfObjectKitImage" + suffix;
        }

        var next = working.Objects.Max(o => o.Id.ObjectNumber) + 1;
        PdfReference Add(PdfValue value)
        {
            if (next >= int.MaxValue)
            {
                throw new InvalidDataException("No object numbers available.");
            }

            var id = new PdfReference(next++);
            working.Objects.Add(new(id, value));
            return id;
        }

        if (image.Mask is not null)
        {
            image.Image.Entries!["SMask"] = Ref(Add(image.Mask));
            if (string.CompareOrdinal(working.PdfVersion, "1.4") < 0)
            {
                working.PdfVersion = "1.4";
            }
        }

        var imageId = Add(image.Image);
        xobjects[name] = Ref(imageId);
        resources["XObject"] = new() { Type = "dictionary", Entries = xobjects };
        page.Dictionary.Entries!["Resources"] = new() { Type = "dictionary", Entries = resources };

        // The initial q and final Q isolate the entire existing content sequence, including
        // persistent transforms and clipping. Original streams and shared resources stay intact.
        var contents = new List<PdfValue> { Ref(Add(Stream("q\n"u8.ToArray()))) };
        contents.AddRange(page.Streams.Select(s => Ref(s.Id)));
        var commands = $"\nQ\nn\nq\n{Syntax(Num(width))} 0 0 {Syntax(Num(height))} {Syntax(Num(x))} {Syntax(Num(y))} cm\n/{name} Do\nQ\n";
        contents.Add(Ref(Add(Stream(Encoding.ASCII.GetBytes(commands)))));
        page.Dictionary.Entries["Contents"] = new() { Type = "array", Items = contents };

        PdfDocumentValidator.Validate(working);
        document.PdfVersion = working.PdfVersion;
        document.Trailer = working.Trailer;
        document.Objects = working.Objects;
        return imageId;
    }

    private static void ValidateContent(NativePage page)
    {
        var depth = 0;
        var inText = false;
        foreach (var (_, stream) in page.Streams)
        {
            foreach (var operation in PdfContent.Parse(PdfStreamDecoder.Decode(stream)))
            {
                switch (operation.Operator)
                {
                    case "q":
                        depth++;
                        break;
                    case "Q":
                        if (--depth < 0)
                        {
                            throw new InvalidDataException("Existing page has an unbalanced graphics state.");
                        }
                        break;
                    case "BT":
                        if (inText)
                        {
                            throw new InvalidDataException("Existing page has nested text objects.");
                        }
                        inText = true;
                        break;
                    case "ET":
                        if (!inText)
                        {
                            throw new InvalidDataException("Existing page has an unmatched ET operator.");
                        }
                        inText = false;
                        break;
                }
            }
        }

        if (depth != 0 || inText)
        {
            throw new InvalidDataException("Existing page must have balanced q/Q and BT/ET operators.");
        }
    }
}
