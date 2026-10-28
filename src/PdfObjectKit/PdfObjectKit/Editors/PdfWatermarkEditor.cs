using System.Text;
using System.Text.Json;
using PdfObjectKit.Parsers;
using PdfObjectKit.Fonts;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Options;
using PdfObjectKit.Extractors;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

/// <summary>Creates, updates and removes marked watermark placement streams without rewriting existing text.</summary>
public static class PdfWatermarkEditor
{
    private const string Marker = "PdfObjectKitWatermark";
    private const string Settings = "PdfObjectKitWatermarkOptions";

    public static string AddText(PdfObjectDocument document, PdfTextWatermarkOptions options)
    {
        var id = Guid.NewGuid().ToString("N");
        PdfPageEditor.Edit(document, working => Add(working, options, id));
        return id;
    }

    public static void UpdateText(PdfObjectDocument document, string id, PdfTextWatermarkOptions options)
    {
        PdfPageEditor.Edit(document, working =>
        {
            RemoveCore(working, id);
            Add(working, options, id);
        });
    }

    public static void Remove(PdfObjectDocument document, string id)
    {
        PdfPageEditor.Edit(document, working => RemoveCore(working, id));
    }

    public static IReadOnlyList<PdfWatermarkInfo> GetWatermarks(PdfObjectDocument document)
    {
        var found = new Dictionary<string, (List<int> Pages, PdfTextWatermarkOptions Options)>();
        if (!document.Objects.Any(obj => obj.Value.Entries?.GetValueOrDefault("Subtype")?.Name == "Form" && Text(obj.Value, Marker) is not null))
        {
            return [];
        }
        var graph = new PdfGraph(document);
        foreach (var page in graph.Pages())
        {
            var visited = new HashSet<PdfValue>();
            void Visit(PdfValue stream, PdfValue resources, int depth)
            {
                if (depth > 100)
                {
                    throw new InvalidDataException("Excessive watermark Form nesting.");
                }
                foreach (var operation in PdfContent.Parse(PdfStreamDecoder.Decode(stream)))
                {
                    if (operation.Operator != "Do" || operation.Operands.LastOrDefault()?.Name is not { } name)
                    {
                        continue;
                    }
                    var xobjects = graph.Get(resources, "XObject");
                    var form = xobjects is null ? null : graph.Get(xobjects, name);
                    if (form?.Entries?.GetValueOrDefault("Subtype")?.Name != "Form" || !visited.Add(form))
                    {
                        continue;
                    }
                    var id = Text(form, Marker);
                    if (id is not null)
                    {
                        if (!found.TryGetValue(id, out var entry))
                        {
                            var settings = Text(form, Settings) ?? throw new InvalidDataException("Missing watermark settings.");
                            entry = ([], JsonSerializer.Deserialize<PdfTextWatermarkOptions>(settings) ?? throw new InvalidDataException("Invalid watermark settings."));
                            found.Add(id, entry);
                        }
                        if (!entry.Pages.Contains(page.Number))
                        {
                            entry.Pages.Add(page.Number);
                        }
                    }
                    else
                    {
                        Visit(form, graph.Get(form, "Resources") ?? resources, depth + 1);
                    }
                }
            }
            foreach (var (_, stream) in page.Streams)
            {
                Visit(stream, page.Resources, 0);
            }
        }
        return found.Select(pair => new PdfWatermarkInfo(pair.Key, pair.Value.Pages, pair.Value.Options with { PageNumbers = pair.Value.Pages })).ToList();
    }

    private static string? Text(PdfValue value, string name) => value.Entries?.GetValueOrDefault(name) is { Type: "string", Base64: { } bytes } ? Encoding.UTF8.GetString(Convert.FromBase64String(bytes)) : null;

    private static void CheckUnsigned(PdfObjectDocument document)
    {
        if (PdfFormExtractor.Extract(document).Any(field => field.IsSigned))
        {
            throw new NotSupportedException("Watermark editing would invalidate existing digital signatures.");
        }
    }

    private static void RemoveCore(PdfObjectDocument document, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        CheckUnsigned(document);
        var changed = false;
        foreach (var obj in document.Objects)
        {
            if (obj.Value.Type == "stream" && obj.Value.Entries?.GetValueOrDefault("Subtype")?.Name == "Form" && Text(obj.Value, Marker) == id)
            {
                obj.Value.Base64 = "";
                obj.Value.Entries!.Remove("Filter");
                obj.Value.Entries.Remove("DecodeParms");
                obj.Value.Entries.Remove(Marker);
                obj.Value.Entries.Remove(Settings);
                changed = true;
            }
        }
        foreach (var page in new PdfGraph(document).Pages())
        {
            var kept = page.Streams.Where(stream => Text(stream.Value, Marker) != id).ToList();
            if (kept.Count != page.Streams.Count)
            {
                page.Dictionary.Entries!["Contents"] = Array(kept.Select(stream => Ref(stream.Id)).ToArray());
                changed = true;
            }
        }
        if (!changed)
        {
            throw new ArgumentException("Watermark not found. Only PdfObjectKit watermarks retaining their metadata can be removed.", nameof(id));
        }
    }

    private static void Add(PdfObjectDocument document, PdfTextWatermarkOptions options, string id)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Text) || options.Text.Length > 1000 || options.Text.Any(c => c < 32 || c > 126)
            || !double.IsFinite(options.FontSize) || options.FontSize <= 0 || options.FontSize > 1000
            || !double.IsFinite(options.Opacity) || options.Opacity < 0 || options.Opacity > 1
            || !double.IsFinite(options.Rotation) || !double.IsFinite(options.Margin) || options.Margin < 0
            || !Enum.IsDefined(options.Position) || !Enum.IsDefined(options.Layer)
            || options.Color is null || !System.Text.RegularExpressions.Regex.IsMatch(options.Color, "^#[0-9a-fA-F]{6}$"))
        {
            throw new ArgumentException("Use printable ASCII text, font size 0-1000, opacity 0-1, a finite angle, nonnegative margin and #RRGGBB color.");
        }
        CheckUnsigned(document);
        var graph = new PdfGraph(document);
        var pages = graph.Pages();
        var selection = options.PageNumbers ?? Enumerable.Range(1, pages.Count).ToArray();
        if (selection.Count == 0 || selection.Distinct().Count() != selection.Count || selection.Any(n => n < 1 || n > pages.Count))
        {
            throw new ArgumentException("Select distinct valid one-based page numbers.");
        }
        PdfValue Append(PdfValue value)
        {
            var reference = new PdfReference(checked(document.Objects.Max(obj => obj.Id.ObjectNumber) + 1));
            document.Objects.Add(new(reference, value));
            return Ref(reference);
        }
        var font = Dict(("Type", Name("Font")), ("Subtype", Name("Type1")), ("BaseFont", Name("Helvetica")));
        var width = new PdfNativeFont(graph, font).Decode(Encoding.ASCII.GetBytes(options.Text)).Sum(g => g.Width) * options.FontSize;
        var height = options.FontSize;
        var rgb = Convert.FromHexString(options.Color[1..]);
        string N(double value) => Syntax(Num(value));
        var form = Stream(Encoding.ASCII.GetBytes($"q /GS gs {N(rgb[0] / 255.0)} {N(rgb[1] / 255.0)} {N(rgb[2] / 255.0)} rg BT /F1 {N(options.FontSize)} Tf 1 0 0 1 0 {N(options.FontSize * .207)} Tm {Syntax(Str(Encoding.ASCII.GetBytes(options.Text)))} Tj ET Q\n"));
        form.Entries!["Type"] = Name("XObject");
        form.Entries["Subtype"] = Name("Form");
        form.Entries["BBox"] = Array(Num(-options.FontSize * .1), Num(0), Num(width + options.FontSize * .1), Num(height * 1.2));
        var state = Dict(("Type", Name("ExtGState")), ("ca", Num(options.Opacity)), ("CA", Num(options.Opacity)));
        form.Entries["Resources"] = Dict(("Font", Dict(("F1", font))), ("ExtGState", Dict(("GS", state))));
        form.Entries[Marker] = Str(Encoding.UTF8.GetBytes(id));
        form.Entries[Settings] = Str(JsonSerializer.SerializeToUtf8Bytes(options));
        var formRef = Append(form);
        foreach (var number in selection)
        {
            var page = pages[number - 1];
            PdfImageEditor.ValidateContent(page);
            PdfValue? Inherit(string key)
            {
                var node = page.Dictionary;
                var seen = new HashSet<PdfValue>();
                while (seen.Add(node) && seen.Count <= 100)
                {
                    if (graph.Get(node, key) is { } value)
                    {
                        return value;
                    }
                    if (graph.Get(node, "Parent") is not { } parent)
                    {
                        return null;
                    }
                    node = parent;
                }
                throw new InvalidDataException("Invalid page inheritance.");
            }
            var media = Inherit("MediaBox")?.Items?.Select(Number).ToArray() ?? [0, 0, 612, 792];
            var crop = Inherit("CropBox")?.Items?.Select(Number).ToArray() ?? media;
            if (media.Length != 4 || crop.Length != 4)
            {
                throw new InvalidDataException("Invalid page box.");
            }
            var left = Math.Max(media[0], crop[0]);
            var bottom = Math.Max(media[1], crop[1]);
            var right = Math.Min(media[2], crop[2]);
            var top = Math.Min(media[3], crop[3]);
            var rotation = Number(Inherit("Rotate") ?? Num(0));
            if (rotation % 90 != 0 || !double.IsFinite(rotation) || right <= left || top <= bottom)
            {
                throw new InvalidDataException("Invalid page geometry.");
            }
            rotation = (rotation % 360 + 360) % 360;
            var w = rotation is 90 or 270 ? top - bottom : right - left;
            var h = rotation is 90 or 270 ? right - left : top - bottom;
            var angle = options.Rotation % 360 * Math.PI / 180;
            var c = Math.Cos(angle);
            var s = Math.Sin(angle);
            var bw = Math.Abs(c) * width + Math.Abs(s) * height;
            var bh = Math.Abs(s) * width + Math.Abs(c) * height;
            var x = options.Position is PdfWatermarkPosition.TopLeft or PdfWatermarkPosition.BottomLeft ? options.Margin + bw / 2 : options.Position is PdfWatermarkPosition.TopRight or PdfWatermarkPosition.BottomRight ? w - options.Margin - bw / 2 : w / 2;
            var y = options.Position is PdfWatermarkPosition.TopLeft or PdfWatermarkPosition.TopRight ? h - options.Margin - bh / 2 : options.Position is PdfWatermarkPosition.BottomLeft or PdfWatermarkPosition.BottomRight ? options.Margin + bh / 2 : h / 2;
            var tx = x - c * width / 2 + s * height / 2;
            var ty = y - s * width / 2 - c * height / 2;
            // Displayed bottom-left coordinates back to unrotated page coordinates.
            var matrix = rotation switch
            {
                90 => $"0 1 -1 0 {N(right)} {N(bottom)}",
                180 => $"-1 0 0 -1 {N(right)} {N(top)}",
                270 => $"0 -1 1 0 {N(left)} {N(top)}",
                _ => $"1 0 0 1 {N(left)} {N(bottom)}"
            };
            var resources = new Dictionary<string, PdfValue>(page.Resources.Entries!);
            var xobjects = new Dictionary<string, PdfValue>(graph.Get(page.Resources, "XObject")?.Entries ?? []);
            var resourceName = "PdfObjectKitWatermark_" + id;
            while (xobjects.ContainsKey(resourceName))
            {
                resourceName += "_";
            }
            xobjects[resourceName] = formRef;
            resources["XObject"] = new() { Type = "dictionary", Entries = xobjects };
            page.Dictionary.Entries!["Resources"] = new() { Type = "dictionary", Entries = resources };
            var placement = Stream(Encoding.ASCII.GetBytes($"q {matrix} cm {N(c)} {N(s)} {N(-s)} {N(c)} {N(tx)} {N(ty)} cm /{resourceName} Do Q\n"));
            placement.Entries![Marker] = Str(Encoding.UTF8.GetBytes(id));
            placement.Entries[Settings] = Str(JsonSerializer.SerializeToUtf8Bytes(options));
            var placementRef = Append(placement);
            var contents = new List<PdfValue>();
            if (options.Layer == PdfWatermarkLayer.Background)
            {
                contents.Add(placementRef);
            }
            contents.Add(Append(Stream("q\n"u8.ToArray())));
            contents.AddRange(page.Streams.Select(stream => Ref(stream.Id)));
            contents.Add(Append(Stream("\nQ\nn\n"u8.ToArray())));
            if (options.Layer == PdfWatermarkLayer.Foreground)
            {
                contents.Add(placementRef);
            }
            page.Dictionary.Entries["Contents"] = Array(contents.ToArray());
        }
        if (Version.Parse(document.PdfVersion) < new Version(1, 4))
        {
            document.PdfVersion = "1.4";
        }
    }
}
