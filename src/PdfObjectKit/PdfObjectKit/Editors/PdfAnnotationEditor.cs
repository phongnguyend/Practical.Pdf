using System.Text;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

/// <summary>Creates standard editable PDF markup annotations with saved appearances.</summary>
public static class PdfAnnotationEditor
{
    public static void Add(PdfObjectDocument document, int pageNumber, PdfAnnotationKind kind,
        IReadOnlyList<PdfAnnotationRegion> regions, string contents = "", string author = "")
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (!Enum.IsDefined(kind) || regions.Count is < 1 or > 1000 || kind == PdfAnnotationKind.Comment && regions.Count != 1)
        {
            throw new ArgumentException("Choose a valid kind and 1–1000 markup regions (one for comments).");
        }
        foreach (var r in regions)
        {
            if (r is null || !new[] { r.X, r.Y, r.Width, r.Height, r.X + r.Width, r.Y + r.Height }.All(double.IsFinite) || r.Width <= 0 || r.Height <= 0)
            {
                throw new ArgumentException("Annotation regions must have finite coordinates and positive dimensions.");
            }
        }
        var text = Text(contents);
        var title = Text(author);
        PdfPageEditor.Edit(document, working =>
        {
            Guard(working);
            var graph = new PdfGraph(working);
            var page = Page(graph, pageNumber);
            var left = regions.Min(r => r.X);
            var bottom = regions.Min(r => r.Y);
            var right = regions.Max(r => r.X + r.Width);
            var top = regions.Max(r => r.Y + r.Height);
            var width = right - left;
            var height = top - bottom;
            if (!double.IsFinite(width) || !double.IsFinite(height))
            {
                throw new ArgumentException("Annotation bounds overflow.");
            }
            var appearance = new StringBuilder("q /GS gs 1 0.8 0 rg 0.85 0.25 0 RG\n");
            var quads = new List<PdfValue>();
            foreach (var r in regions)
            {
                var x = r.X - left;
                var y = r.Y - bottom;
                string N(double v) => Syntax(Num(v));
                // Acrobat/PDF.js text markup uses top-left, top-right, bottom-left, bottom-right.
                quads.AddRange(new[] { r.X, r.Y + r.Height, r.X + r.Width, r.Y + r.Height, r.X, r.Y, r.X + r.Width, r.Y }.Select(Num));
                if (kind == PdfAnnotationKind.Highlight)
                {
                    appearance.Append($"{N(x)} {N(y)} {N(r.Width)} {N(r.Height)} re f\n");
                }
                else if (kind == PdfAnnotationKind.Comment)
                {
                    appearance.Append($"0 0 {N(width)} {N(height)} re f 0.3 G 1 w\n");
                    for (var line = 1; line <= 3; line++)
                    {
                        appearance.Append($"{N(width * .2)} {N(height * line / 5)} m {N(width * .8)} {N(height * line / 5)} l S\n");
                    }
                }
                else
                {
                    var thickness = Math.Min(r.Height, Math.Max(.5, r.Height / 16));
                    var baseline = y + (kind == PdfAnnotationKind.Underline ? thickness / 2 : r.Height / 2);
                    appearance.Append($"{N(thickness)} w {N(x)} {N(baseline)} m {N(x + r.Width)} {N(baseline)} l S\n");
                }
            }
            appearance.Append("Q");
            var ap = Stream(Encoding.ASCII.GetBytes(appearance.ToString()));
            ap.Entries!["Type"] = Name("XObject");
            ap.Entries["Subtype"] = Name("Form");
            ap.Entries["BBox"] = Array(Num(0), Num(0), Num(width), Num(height));
            ap.Entries["Resources"] = Dict(("ExtGState", Dict(("GS", Dict(("Type", Name("ExtGState")),
                ("ca", Num(kind == PdfAnnotationKind.Highlight ? .35 : 1)), ("CA", Num(1)), ("BM", Name(kind == PdfAnnotationKind.Highlight ? "Multiply" : "Normal")))))));
            var pageId = working.Objects.Single(item => ReferenceEquals(item.Value, page)).Id;
            var node = Dict(("Type", Name("Annot")), ("Subtype", Name(kind == PdfAnnotationKind.Comment ? "Text" : kind.ToString())),
                ("Rect", Array(Num(left), Num(bottom), Num(right), Num(top))), ("P", Ref(pageId)),
                ("Contents", text), ("T", title), ("F", Num(4)), ("C", kind is PdfAnnotationKind.Comment or PdfAnnotationKind.Highlight ? Array(Num(1), Num(.8), Num(0)) : Array(Num(.85), Num(.25), Num(0))),
                ("NM", Text(Guid.NewGuid().ToString())), ("AP", Dict(("N", AddObject(working, ap)))));
            if (kind == PdfAnnotationKind.Comment)
            {
                node.Entries!["Name"] = Name("Comment");
            }
            else
            {
                node.Entries!["QuadPoints"] = Array(quads.ToArray());
            }
            // Copy the page array so a shared Annots array is not changed on other pages.
            page.Entries!["Annots"] = Array([.. graph.Get(page, "Annots")?.Items ?? [], AddObject(working, node)]);
        });
    }

    public static void UpdateContents(PdfObjectDocument document, int pageNumber, int annotationNumber, string contents)
    {
        var text = Text(contents);
        PdfPageEditor.Edit(document, working =>
        {
            Guard(working);
            var graph = new PdfGraph(working);
            var node = Annotation(graph, Page(graph, pageNumber), annotationNumber);
            node.Entries!["Contents"] = text;
            node.Entries.Remove("RC");
        });
    }

    public static void Delete(PdfObjectDocument document, int pageNumber, int annotationNumber)
    {
        PdfPageEditor.Edit(document, working =>
        {
            Guard(working);
            var graph = new PdfGraph(working);
            var page = Page(graph, pageNumber);
            var node = Annotation(graph, page, annotationNumber);
            if (graph.Get(node, "StructParent") is not null)
            {
                throw new NotSupportedException("Deleting a tagged annotation requires updating its structure tree.");
            }
            var items = graph.Get(page, "Annots")!.Items!;
            var popup = graph.Get(node, "Popup");
            if (items.Any(item => ReferenceEquals(graph.Get(item, "IRT"), node)))
            {
                throw new NotSupportedException("Delete annotation replies first.");
            }
            page.Entries!["Annots"] = Array(items.Where((item, index) => index != annotationNumber - 1 && !ReferenceEquals(graph.Resolve(item), popup)).ToArray());
        });
    }

    private static PdfValue Annotation(PdfGraph graph, PdfValue page, int number)
    {
        var items = graph.Get(page, "Annots")?.Items;
        if (items is null || number < 1 || number > items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }
        var node = graph.Resolve(items[number - 1]);
        if (graph.Get(node, "Subtype")?.Name is not ("Text" or "Highlight" or "Underline" or "StrikeOut") || ((int)graph.Number(node, "F") & (64 | 128 | 512)) != 0)
        {
            throw new NotSupportedException("Choose an editable comment or text markup annotation.");
        }
        return node;
    }

    private static PdfValue Page(PdfGraph graph, int number)
    {
        var pages = graph.Pages();
        if (number < 1 || number > pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }
        return pages[number - 1].Dictionary;
    }

    private static void Guard(PdfObjectDocument document)
    {
        if (PdfFormExtractor.Extract(document).Any(field => field.IsSigned))
        {
            throw new NotSupportedException("Annotation editing would invalidate the document signature.");
        }
    }

    private static PdfValue Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 10000)
        {
            throw new ArgumentException("Annotation text is limited to 10000 characters.");
        }
        return Str([0xfe, 0xff, .. new UnicodeEncoding(true, false, true).GetBytes(value)]);
    }

    private static PdfValue AddObject(PdfObjectDocument document, PdfValue value)
    {
        var id = new PdfReference(checked(document.Objects.Max(item => item.Id.ObjectNumber) + 1));
        document.Objects.Add(new(id, value));
        return Ref(id);
    }
}
