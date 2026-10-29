using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Options;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

/// <summary>Paints saved normal appearances into page content. Returns an independent document.</summary>
public static class PdfFlattenEditor
{
    public static PdfObjectDocument Flatten(PdfObjectDocument document, PdfFlattenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new();
        if (!options.FormFields && !options.Annotations)
        {
            throw new ArgumentException("Choose form fields, annotations, or both.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (PdfFormExtractor.Extract(document).Any(field => field.IsSigned)
            || document.Objects.Any(item => ContainsSignature(item.Value)))
        {
            throw new NotSupportedException("Flattening would invalidate existing digital signatures.");
        }
        var working = PdfConverter.FromJson(PdfConverter.ToJson(document));
        var graph = new PdfGraph(working);
        var catalog = graph.Resolve(working.Trailer.Entries!["Root"]);
        var acro = graph.Get(catalog, "AcroForm");
        var structure = new PdfFlattenStructure(graph, catalog);
        if (options.FormFields && acro is not null && graph.Get(acro, "NeedAppearances")?.Boolean == true)
        {
            throw new NotSupportedException("This form requests regenerated appearances. Save updated field appearances before flattening.");
        }
        var next = working.Objects.Max(item => item.Id.ObjectNumber);
        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(checked(++next));
            working.Objects.Add(new(id, value));
            return Ref(id);
        }
        foreach (var page in graph.Pages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var annotations = graph.Get(page.Dictionary, "Annots")?.Items ?? [];
            var selected = annotations.Select(graph.Resolve).Where(annotation =>
            {
                var subtype = graph.Get(annotation, "Subtype")?.Name;
                return subtype == "Widget" ? options.FormFields : options.Annotations && subtype is not ("Link" or "Popup");
            }).ToHashSet();
            if (selected.Count == 0)
            {
                continue;
            }
            PdfImageEditor.ValidateContent(page);
            var resources = Dict();
            resources.Entries = new(page.Resources.Entries!);
            var xobjects = Dict();
            xobjects.Entries = new(graph.Get(page.Resources, "XObject")?.Entries ?? []);
            resources.Entries["XObject"] = xobjects;
            var overlay = new StringBuilder();
            foreach (var raw in annotations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var annotation = graph.Resolve(raw);
                if (!selected.Contains(annotation))
                {
                    continue;
                }
                var subtype = graph.Get(annotation, "Subtype")?.Name;
                if (subtype is not ("Widget" or "Text" or "Highlight" or "Underline" or "StrikeOut" or "Squiggly" or "FreeText" or "Stamp" or "Ink" or "Square" or "Circle" or "Line" or "Polygon" or "PolyLine" or "Caret"))
                {
                    throw new NotSupportedException($"Page {page.Number}: annotation type '{subtype}' cannot be flattened.");
                }
                var flags = checked((int)graph.Number(annotation, "F"));
                if ((flags & 3) != 0)
                {
                    if (structure.IsTagged(annotation))
                    {
                        throw new NotSupportedException("Hidden tagged annotations cannot be flattened without changing their accessibility semantics.");
                    }
                    continue; // Hidden/invisible objects are removed without painting.
                }
                if ((flags & (8 | 16 | 32 | 256)) != 0 || graph.Get(annotation, "OC") is not null || graph.Number(annotation, "CA", 1) != 1)
                {
                    throw new NotSupportedException($"Page {page.Number}: special visibility, rotation, zoom or opacity rules cannot be flattened safely.");
                }
                var normal = graph.Get(annotation, "AP") is { } ap ? graph.Get(ap, "N") : null;
                if (normal?.Type == "dictionary")
                {
                    var state = graph.Get(annotation, "AS")?.Name;
                    normal = state is not null && normal.Entries!.TryGetValue(state, out var appearance) ? graph.Resolve(appearance) : null;
                }
                if (normal?.Type != "stream")
                {
                    throw new NotSupportedException($"Page {page.Number}: {subtype} has no saved normal appearance for its current state. Regenerate/save the appearance first.");
                }
                var rect = Numbers(graph, graph.Get(annotation, "Rect"), 4);
                var box = Numbers(graph, graph.Get(normal, "BBox"), 4);
                var matrix = graph.Get(normal, "Matrix") is { } m ? Numbers(graph, m, 6) : [1d, 0, 0, 1, 0, 0];
                if (box[2] <= box[0] || box[3] <= box[1] || matrix[0] * matrix[3] - matrix[1] * matrix[2] == 0 || normal.Entries!.ContainsKey("F"))
                {
                    throw new NotSupportedException("Flattening requires a nonempty appearance with an invertible matrix and embedded stream data.");
                }
                var corners = new[] { (box[0], box[1]), (box[2], box[1]), (box[2], box[3]), (box[0], box[3]) }
                    .Select(p => (X: matrix[0] * p.Item1 + matrix[2] * p.Item2 + matrix[4], Y: matrix[1] * p.Item1 + matrix[3] * p.Item2 + matrix[5])).ToArray();
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
                    throw new InvalidDataException("Flattened appearances require nonempty bounds.");
                }
                var sx = w / width;
                var sy = h / height;
                var tx = x - sx * left;
                var ty = y - sy * bottom;
                var copy = new PdfValue { Type = "stream", Base64 = normal.Base64, Entries = new(normal.Entries!) };
                if (copy.Entries.ContainsKey("StructParents") || copy.Entries.ContainsKey("StructParent"))
                {
                    throw new NotSupportedException("An appearance with its own structure tags requires additional remapping.");
                }
                copy.Entries["Type"] = Name("XObject");
                copy.Entries["Subtype"] = Name("Form");
                if (graph.Get(normal, "Resources") is null)
                {
                    copy.Entries["Resources"] = subtype == "Widget" && acro is not null ? graph.Get(acro, "DR") ?? page.Resources : page.Resources;
                }
                var name = "PdfObjectKitFlatten" + (next + 1);
                while (xobjects.Entries.ContainsKey(name))
                {
                    name += "_";
                }
                var appearanceRef = Add(copy);
                var wrapper = Stream("/Span <</MCID 0>> BDC /Appearance Do EMC\n"u8.ToArray());
                wrapper.Entries!["Type"] = Name("XObject");
                wrapper.Entries["Subtype"] = Name("Form");
                wrapper.Entries["BBox"] = Array(Num(left), Num(bottom), Num(left + width), Num(bottom + height));
                wrapper.Entries["Resources"] = Dict(("XObject", Dict(("Appearance", appearanceRef))));
                var wrapperRef = Add(wrapper);
                var pageRef = Ref(working.Objects.Single(item => ReferenceEquals(item.Value, page.Dictionary)).Id);
                var parentKey = structure.Convert(annotation, pageRef, wrapperRef);
                if (parentKey is { } key)
                {
                    wrapper.Entries["StructParents"] = Num(key);
                }
                xobjects.Entries[name] = parentKey is null ? appearanceRef : wrapperRef;
                string N(double value) => Syntax(Num(value));
                overlay.Append($"q {N(x)} {N(y)} {N(w)} {N(h)} re W n {N(sx)} 0 0 {N(sy)} {N(tx)} {N(ty)} cm /{name} Do Q\n");
            }
            var remaining = annotations.Where(raw =>
            {
                var annotation = graph.Resolve(raw);
                var popupRemoved = graph.Get(annotation, "Subtype")?.Name == "Popup" && graph.Get(annotation, "Parent") is { } parent && selected.Contains(parent);
                if (popupRemoved && structure.IsTagged(annotation))
                {
                    throw new NotSupportedException("A tagged popup cannot be removed without changing its accessibility semantics.");
                }
                return !selected.Contains(annotation) && !popupRemoved;
            }).ToArray();
            if (remaining.Length == 0)
            {
                page.Dictionary.Entries!.Remove("Annots");
            }
            else
            {
                page.Dictionary.Entries!["Annots"] = Array(remaining);
            }
            if (overlay.Length > 0)
            {
                page.Dictionary.Entries["Resources"] = resources;
                page.Dictionary.Entries["Contents"] = Array([Add(Stream("q\n"u8.ToArray())), .. page.Streams.Select(stream => Ref(stream.Id)), Add(Stream(Encoding.ASCII.GetBytes("\nQ n\n" + overlay)))]);
            }
        }
        if (options.FormFields)
        {
            catalog.Entries!.Remove("AcroForm");
        }
        structure.Finish();
        PdfDocumentCompressor.Prune(working, cancellationToken);
        return working;
    }

    private static double[] Numbers(PdfGraph graph, PdfValue? value, int count)
    {
        var numbers = value?.Items?.Select(item => Number(graph.Resolve(item))).ToArray();
        if (numbers?.Length != count || numbers.Any(number => !double.IsFinite(number)))
        {
            throw new InvalidDataException("Invalid appearance geometry.");
        }
        return numbers;
    }

    private static bool ContainsSignature(PdfValue value)
    {
        return value.Entries?.ContainsKey("ByteRange") == true
            || (value.Entries?.Values.Any(ContainsSignature) ?? false)
            || (value.Items?.Any(ContainsSignature) ?? false);
    }
}
