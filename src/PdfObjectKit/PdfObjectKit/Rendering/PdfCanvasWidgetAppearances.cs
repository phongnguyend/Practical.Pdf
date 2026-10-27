using System.Text;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Rendering;

internal static class PdfCanvasWidgetAppearances
{
    // Use virtual Form invocations so the existing interpreter handles fonts, text matrices,
    // paths and nested resources identically to page content. Never modify the input graph.
    internal static (PdfGraph Graph, NativePage Page) Prepare(PdfObjectDocument document, PdfGraph graph, NativePage page, HashSet<string> warnings)
    {
        var annotations = graph.Get(page.Dictionary, "Annots")?.Items ?? [];
        if (annotations.Count == 0)
        {
            return (graph, page);
        }
        var objects = new List<PdfIndirectObject>(document.Objects);
        var next = objects.Max(item => item.Id.ObjectNumber);
        PdfReference Add(PdfValue value)
        {
            var id = new PdfReference(checked(++next));
            objects.Add(new(id, value));
            return id;
        }
        var resources = new PdfValue { Type = "dictionary", Entries = new(page.Resources.Entries ?? []) };
        var xobjects = new PdfValue { Type = "dictionary", Entries = new(graph.Get(page.Resources, "XObject")?.Entries ?? []) };
        resources.Entries!["XObject"] = xobjects;
        var overlay = new StringBuilder();
        if (annotations.Count > 10000)
        {
            throw new NotSupportedException("Page annotation limit exceeded.");
        }
        foreach (var raw in annotations)
        {
            try
            {
                var widget = graph.Resolve(raw);
                if (graph.Get(widget, "Subtype")?.Name != "Widget")
                {
                    continue;
                }
                var flags = checked((int)graph.Number(widget, "F"));
                if ((flags & (1 | 2 | 32)) != 0)
                {
                    continue; // Invisible, Hidden or NoView; this exporter renders for screen display.
                }
                if ((flags & (8 | 16)) != 0)
                {
                    warnings.Add("Widget NoZoom/NoRotate flags are not supported.");
                }
                var normal = graph.Get(widget, "AP") is { } ap ? graph.Get(ap, "N") : null;
                if (normal?.Type == "dictionary")
                {
                    var state = graph.Get(widget, "AS")?.Name;
                    normal = state is not null && normal.Entries!.TryGetValue(state, out var selected) ? graph.Resolve(selected) : null;
                }
                if (normal?.Type != "stream")
                {
                    warnings.Add("A widget has no usable saved normal appearance; regenerate its appearance to render it.");
                    continue;
                }
                double[] Numbers(PdfValue? value, int count)
                {
                    var numbers = value?.Items?.Select(item => Number(graph.Resolve(item))).ToArray();
                    if (numbers?.Length != count || numbers.Any(number => !double.IsFinite(number)))
                    {
                        throw new InvalidDataException("Invalid widget appearance geometry.");
                    }
                    return numbers;
                }
                var rect = Numbers(graph.Get(widget, "Rect"), 4);
                var box = Numbers(graph.Get(normal, "BBox"), 4);
                var matrix = graph.Get(normal, "Matrix") is { } m ? Numbers(m, 6) : [1d, 0, 0, 1, 0, 0];
                var corners = new[] { (box[0], box[1]), (box[2], box[1]), (box[2], box[3]), (box[0], box[3]) }
                    .Select(p => (X: matrix[0] * p.Item1 + matrix[2] * p.Item2 + matrix[4], Y: matrix[1] * p.Item1 + matrix[3] * p.Item2 + matrix[5])).ToList();
                var left = corners.Min(p => p.X);
                var bottom = corners.Min(p => p.Y);
                var width = corners.Max(p => p.X) - left;
                var height = corners.Max(p => p.Y) - bottom;
                var x = Math.Min(rect[0], rect[2]);
                var y = Math.Min(rect[1], rect[3]);
                var w = Math.Abs(rect[2] - rect[0]);
                var h = Math.Abs(rect[3] - rect[1]);
                if (width <= 0 || height <= 0 || w <= 0 || h <= 0)
                {
                    throw new InvalidDataException("Widget appearance requires nonempty bounds.");
                }
                var sx = w / width;
                var sy = h / height;
                var tx = x - sx * left;
                var ty = y - sy * bottom;
                if (!new[] { sx, sy, tx, ty }.All(double.IsFinite))
                {
                    throw new InvalidDataException("Invalid widget appearance transform.");
                }
                var appearance = new PdfValue { Type = "stream", Base64 = normal.Base64, Entries = new(normal.Entries!) };
                appearance.Entries["Subtype"] = Name("Form");
                appearance.Entries["BBox"] = Array(box.Select(Num).ToArray());
                appearance.Entries["Matrix"] = Array(matrix.Select(Num).ToArray());
                if (graph.Get(normal, "Resources") is null)
                {
                    var catalog = graph.Resolve(document.Trailer.Entries!["Root"]);
                    var acro = graph.Get(catalog, "AcroForm");
                    appearance.Entries["Resources"] = acro is null ? page.Resources : graph.Get(acro, "DR") ?? page.Resources;
                }
                var id = Add(appearance);
                var name = "PdfObjectKitWidget" + id.ObjectNumber;
                while (xobjects.Entries!.ContainsKey(name))
                {
                    name += "_";
                }
                xobjects.Entries![name] = Ref(id);
                string N(double value) => Syntax(Num(value));
                overlay.Append($"q {N(x)} {N(y)} {N(w)} {N(h)} re W n {N(sx)} 0 0 {N(sy)} {N(tx)} {N(ty)} cm /{name} Do Q\n");
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
            {
                warnings.Add($"Widget appearance omitted: {ex.Message}");
            }
        }
        if (overlay.Length == 0)
        {
            return (graph, page);
        }
        var start = Stream(Encoding.ASCII.GetBytes("q"));
        var end = Stream(Encoding.ASCII.GetBytes("Q n\n" + overlay));
        var startId = Add(start);
        var endId = Add(end);
        var renderGraph = new PdfGraph(new PdfObjectDocument { Trailer = document.Trailer, Objects = objects });
        return (renderGraph, page with { Resources = resources, Streams = [(startId, start), .. page.Streams, (endId, end)] });
    }
}
