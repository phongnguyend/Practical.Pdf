using System.IO.Compression;
using System.Text;
using PdfObjectKit.Graphs;
using PdfObjectKit.Fonts;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using PdfObjectKit.Rendering;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

/// <summary>Conservative content-preserving redaction for a validated subset of PDF graphics.
/// Unsupported graphics fail atomically. Output is rebuilt without source document structures.</summary>
public static class PdfContentRedactor
{
    private sealed record State(PdfMatrix Matrix, double LineWidth = 1, double MiterLimit = 10, int RenderingMode = 0, double FillAlpha = 1, double StrokeAlpha = 1, string FillColorSpace = "DeviceGray", string StrokeColorSpace = "DeviceGray");

    public static PdfObjectDocument Apply(PdfObjectDocument document, IReadOnlyList<PdfContentRedaction> regions, CancellationToken cancellationToken = default)
    {
        return ApplyCore(document, regions, null, cancellationToken);
    }

    /// <summary>Runs the same validation and removal decisions as Apply without changing the source document.</summary>
    public static IReadOnlyList<PdfRedactionImpact> Preview(PdfObjectDocument document, IReadOnlyList<PdfContentRedaction> regions, CancellationToken cancellationToken = default)
    {
        var impacts = new List<PdfRedactionImpact>();
        ApplyCore(document, regions, impacts, cancellationToken);
        return impacts;
    }

    private static PdfObjectDocument ApplyCore(PdfObjectDocument document, IReadOnlyList<PdfContentRedaction> regions, List<PdfRedactionImpact>? impacts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(regions);
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePages = new PdfGraph(document).Pages();
        if (regions.Count is < 1 or > 1000 || sourcePages.Count > 100)
        {
            throw new ArgumentException("Provide 1-1000 redactions in a document of at most 100 pages.");
        }
        foreach (var region in regions)
        {
            if (region is null || region.PageNumber < 1 || region.PageNumber > sourcePages.Count
                || !double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) || !double.IsFinite(region.Height)
                || region.Width <= 0 || region.Height <= 0 || !double.IsFinite(region.X + region.Width) || !double.IsFinite(region.Y + region.Height))
            {
                throw new ArgumentException("Invalid redaction page or rectangle.");
            }
        }
        // Materialize saved widget and markup appearances before processing their content.
        // Flatten rejects signed/XFA documents and unavailable appearances.
        var working = PdfFlattenEditor.Flatten(document, cancellationToken: cancellationToken);
        var graph = new PdfGraph(working);
        var pages = graph.Pages();
        var parsed = pages.Select(page => new PdfPageInterpreter(graph, page)).ToArray();
        if (parsed.Any(page => page.TextWarning is not null))
        {
            throw Unsupported("unreliable text interpretation");
        }
        var outlineBounds = new Dictionary<PdfValue, Dictionary<string, PdfGlyphBounds>>();
        foreach (var group in parsed.SelectMany(page => page.Slots).GroupBy(slot => slot.Font.Source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (graph.Get(group.Key, "Subtype")?.Name is not ("TrueType" or "Type0"))
            {
                continue;
            }
            try
            {
                var builder = new PdfCanvasFontBuilder(graph, group.First().Font, group.SelectMany(slot => slot.Glyphs));
                outlineBounds[group.Key] = builder.GlyphBounds;
            }
            catch (Exception exception) when (exception is NotSupportedException or InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                // Unresolvable outlines retain the conservative font-wide fallback.
                // Export independently validates fonts required by remaining text.
            }
        }
        var slotIndices = parsed.Select(page => page.Slots.ToDictionary(slot => (slot.Stream, slot.Operation, slot.FormOccurrenceId, slot.Token))).ToArray();
        var removedGlyphs = new HashSet<(PdfReference Stream, int Operation, int Token, int Glyph)>();
        var removedImages = new HashSet<PdfValue>();
        var removedPaths = new HashSet<(PdfReference Stream, int Operation)>();
        var kids = Array();
        var tree = Dict(("Type", Name("Pages")), ("Kids", kids), ("Count", Num(pages.Count)));
        var result = new PdfObjectDocument
        {
            Trailer = Dict(("Root", Ref(new(1)))),
            Objects = [new(new(1), Dict(("Type", Name("Catalog")), ("Pages", Ref(new(2))))), new(new(2), tree)]
        };
        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(result.Objects.Count + 1);
            result.Objects.Add(new(id, value));
            return Ref(id);
        }
        bool Intersects(int pageNumber, IEnumerable<PdfImagePoint> corners, double padding = 0)
        {
            var points = corners.ToArray();
            if (points.Length == 0)
            {
                return false;
            }
            if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)) || !double.IsFinite(padding))
            {
                throw Unsupported("non-finite graphics geometry");
            }
            var x0 = points.Min(point => point.X) - padding;
            var y0 = points.Min(point => point.Y) - padding;
            var x1 = points.Max(point => point.X) + padding;
            var y1 = points.Max(point => point.Y) + padding;
            return regions.Any(region => region.PageNumber == pageNumber && region.X <= x1 && region.X + region.Width >= x0 && region.Y <= y1 && region.Y + region.Height >= y0);
        }
        void Report(int pageNumber, string kind, IEnumerable<PdfImagePoint> bounds, double padding = 0)
        {
            if (impacts is null)
            {
                return;
            }
            var points = bounds.ToArray();
            if (points.Length == 0)
            {
                return;
            }
            if (impacts.Count >= 20000)
            {
                throw Unsupported("preview exceeds 20,000 affected areas; preview fewer regions at a time");
            }
            var x = points.Min(point => point.X) - padding;
            var y = points.Min(point => point.Y) - padding;
            impacts.Add(new(pageNumber, kind, x, y, points.Max(point => point.X) + padding - x, points.Max(point => point.Y) + padding - y));
        }
        static IEnumerable<PdfImagePoint> Corners(PdfMatrix matrix, double x0, double y0, double x1, double y1) =>
            [matrix.Point(x0, y0), matrix.Point(x1, y0), matrix.Point(x1, y1), matrix.Point(x0, y1)];
        var fontCandidates = new Dictionary<PdfValue, List<(PdfNativeFont Font, NativeGlyph Glyph, (PdfReference Stream, int Operation, int Token, int Glyph) Key)>>();
        var rebuiltFonts = new Dictionary<PdfValue, PdfValue>();
        var fontsReady = false;
        PdfValue Font(PdfValue source)
        {
            source = graph.Resolve(source);
            if (fontsReady && rebuiltFonts.TryGetValue(source, out var cached))
            {
                return cached;
            }
            var retained = fontsReady && fontCandidates.TryGetValue(source, out var candidates)
                ? candidates.Where(item => !removedGlyphs.Contains(item.Key)).ToArray() : [];
            if (fontsReady && graph.Get(source, "Subtype")?.Name is "Type0" or "TrueType")
            {
                if (retained.Length == 0)
                {
                    return Dict(("Type", Name("Font")), ("Subtype", Name("Type1")), ("BaseFont", Name("Helvetica")));
                }
                var rebuilt = PdfRedactionEmbeddedFont.Build(graph, retained[0].Font, retained.Select(item => item.Glyph).ToArray(), Add);
                rebuiltFonts.Add(source, rebuilt);
                return rebuilt;
            }
            var name = graph.Get(source, "BaseFont")?.Name;
            if (graph.Get(source, "Subtype")?.Name != "Type1" || name is not ("Helvetica" or "Helvetica-Bold" or "Helvetica-Oblique" or "Helvetica-BoldOblique" or "Courier" or "Courier-Bold" or "Courier-Oblique" or "Courier-BoldOblique" or "Times-Roman" or "Times-Bold" or "Times-Italic" or "Times-BoldItalic")
                || graph.Get(source, "FontDescriptor") is not null
                || graph.Get(source, "Encoding") is { } encoding && encoding.Name is not ("WinAnsiEncoding" or "MacRomanEncoding" or "StandardEncoding"))
            {
                throw Unsupported("embedded/custom fonts or character mappings");
            }
            var font = Dict(("Type", Name("Font")), ("Subtype", Name("Type1")), ("BaseFont", Name(name!)));
            foreach (var key in new[] { "Encoding", "FirstChar", "LastChar", "Widths" })
            {
                if (graph.Get(source, key) is { } value)
                {
                    font.Entries![key] = CleanValue(value);
                }
            }
            if (fontsReady)
            {
                if (graph.Get(source, "ToUnicode") is not null && retained.Length > 0)
                {
                    var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /RedactedUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\n");
                    foreach (var chunk in retained.Select(item => item.Glyph).DistinctBy(glyph => Convert.ToHexString(glyph.Bytes)).Chunk(100))
                    {
                        cmap.Append(chunk.Length).Append(" beginbfchar\n");
                        foreach (var glyph in chunk)
                        {
                            cmap.Append('<').Append(Convert.ToHexString(glyph.Bytes)).Append("> <").Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(glyph.Text))).Append(">\n");
                        }
                        cmap.Append("endbfchar\n");
                    }
                    cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
                    font.Entries!["ToUnicode"] = Add(Stream(Encoding.ASCII.GetBytes(cmap.ToString())));
                }
                rebuiltFonts[source] = font;
            }
            return font;
        }
        PdfValue CleanValue(PdfValue value, int depth = 0)
        {
            if (depth > 16)
            {
                throw Unsupported("nested resource values");
            }
            value = graph.Resolve(value);
            return value.Type switch
            {
                "number" => Num(Number(value)),
                "name" => Name(value.Name!),
                "array" => Array(value.Items!.Select(item => CleanValue(item, depth + 1)).ToArray()),
                _ => throw Unsupported("unexpected resource value")
            };
        }
        PdfValue GraphicsState(PdfValue source)
        {
            source = graph.Resolve(source);
            if (source.Type != "dictionary" || source.Entries is null)
            {
                throw Unsupported("invalid ExtGState dictionary");
            }
            var clean = Dict();
            foreach (var entry in source.Entries)
            {
                var value = graph.Resolve(entry.Value);
                if (entry.Key == "BM" && value.Items is { Count: 1 } blend)
                {
                    value = graph.Resolve(blend[0]);
                }
                var number = value.Type == "number" ? Number(value) : double.NaN;
                var valid = entry.Key switch
                {
                    "Type" => value.Name == "ExtGState",
                    "LW" => double.IsFinite(number) && number >= 0,
                    "ML" => double.IsFinite(number) && number >= 1,
                    "LC" or "LJ" => number is 0 or 1 or 2,
                    "CA" or "ca" => double.IsFinite(number) && number >= 0 && number <= 1,
                    "BM" => value.Name is "Normal" or "Compatible" or "Multiply" or "Screen" or "Overlay" or "Darken" or "Lighten" or "ColorDodge" or "ColorBurn" or "HardLight" or "SoftLight" or "Difference" or "Exclusion" or "Hue" or "Saturation" or "Color" or "Luminosity",
                    "FL" => double.IsFinite(number) && number >= 0 && number <= 100,
                    "RI" => value.Name is "AbsoluteColorimetric" or "RelativeColorimetric" or "Saturation" or "Perceptual",
                    "D" => ValidDash(value),
                    "SMask" => value.Name == "None",
                    "AIS" or "TK" or "SA" => value.Type == "boolean",
                    "OP" or "op" => value.Type == "boolean" && value.Boolean == false,
                    "OPM" => number == 0,
                    _ => false
                };
                if (!valid)
                {
                    throw Unsupported("ExtGState /" + entry.Key);
                }
                clean.Entries![entry.Key] = value.Type == "boolean"
                    ? new PdfValue { Type = "boolean", Boolean = value.Boolean }
                    : CleanValue(value);
            }
            return clean;
        }
        bool ValidDash(PdfValue value)
        {
            if (value.Items is not { Count: 2 } dash || graph.Resolve(dash[0]).Items is not { } lengths
                || graph.Resolve(dash[1]).Type != "number" || !double.IsFinite(Number(graph.Resolve(dash[1]))))
            {
                return false;
            }
            return lengths.All(item => graph.Resolve(item).Type == "number" && double.IsFinite(Number(graph.Resolve(item))) && Number(graph.Resolve(item)) >= 0)
                && (lengths.Count == 0 || lengths.Any(item => Number(graph.Resolve(item)) > 0));
        }
        string DeviceColorSpace(PdfValue value, PdfValue resources)
        {
            value = graph.Resolve(value);
            if (value.Name is "DeviceGray" or "DeviceRGB" or "DeviceCMYK")
            {
                return value.Name;
            }
            if (value.Type == "name" && graph.Get(resources, "ColorSpace")?.Entries?.GetValueOrDefault(value.Name!) is { } alias)
            {
                var resolved = graph.Resolve(alias);
                if (resolved.Name is "DeviceGray" or "DeviceRGB" or "DeviceCMYK")
                {
                    return resolved.Name;
                }
            }
            throw Unsupported("non-device color space");
        }
        PdfValue? TransparencyGroup(PdfValue form, PdfValue resources)
        {
            if (graph.Get(form, "Group") is not { } group)
            {
                return null;
            }
            if (group.Type != "dictionary" || graph.Get(group, "S")?.Name != "Transparency")
            {
                throw Unsupported("Form group type");
            }
            var clean = Dict(("S", Name("Transparency")));
            foreach (var entry in group.Entries!)
            {
                var value = graph.Resolve(entry.Value);
                switch (entry.Key)
                {
                    case "S":
                        break;
                    case "Type" when value.Name == "Group":
                        clean.Entries!["Type"] = Name("Group");
                        break;
                    case "CS":
                        clean.Entries!["CS"] = Name(DeviceColorSpace(value, resources));
                        break;
                    case "I" or "K" when value.Type == "boolean":
                        clean.Entries![entry.Key] = new PdfValue { Type = "boolean", Boolean = value.Boolean };
                        break;
                    default:
                        throw Unsupported("Form group /" + entry.Key);
                }
            }
            return clean;
        }
        long imageBytes = 0;
        var retainedImages = new Dictionary<PdfValue, PdfValue>();
        PdfValue Image(PdfValue source, bool isMask = false)
        {
            var width = graph.Number(source, "Width");
            var height = graph.Number(source, "Height");
            var color = graph.Get(source, "ColorSpace")?.Name;
            if (width != Math.Truncate(width) || height != Math.Truncate(height) || width <= 0 || height <= 0 || width * height > 16000000
                || graph.Number(source, "BitsPerComponent") != 8 || color is not ("DeviceRGB" or "DeviceGray" or "DeviceCMYK")
                || graph.Get(source, "ImageMask") is { } imageMask && (imageMask.Type != "boolean" || imageMask.Boolean != false)
                || new[] { "Mask", "F", "Alternates", "OPI", "OC" }.Any(key => graph.Get(source, key) is not null)
                || isMask && graph.Get(source, "SMask") is not null)
            {
                throw Unsupported("image masks, external data or unsupported image samples");
            }
            byte[] pixels;
            try
            {
                pixels = PdfStreamDecoder.Decode(source);
            }
            catch (NotSupportedException)
            {
                throw Unsupported("retained image codec");
            }
            var channels = color == "DeviceRGB" ? 3 : color == "DeviceCMYK" ? 4 : 1;
            if (pixels.LongLength != (long)width * (long)height * channels)
            {
                throw Unsupported("image sample length mismatch");
            }
            imageBytes += pixels.LongLength;
            if (imageBytes > 64 * 1024 * 1024)
            {
                throw Unsupported("64 MB decoded image budget exceeded");
            }
            if (graph.Get(source, "Decode") is { } decode)
            {
                if (decode.Items is not { } values || values.Count != channels * 2
                    || values.Any(value => value.Type != "number" || Number(value) is not (0 or 1)))
                {
                    throw Unsupported("image Decode values other than default/inverted samples");
                }
                for (var channel = 0; channel < channels; channel++)
                {
                    if (Number(values[channel * 2]) == Number(values[channel * 2 + 1]))
                    {
                        throw Unsupported("constant image Decode ranges");
                    }
                    if (Number(values[channel * 2]) == 1)
                    {
                        for (var index = channel; index < pixels.Length; index += channels)
                        {
                            pixels[index] = (byte)(255 - pixels[index]);
                        }
                    }
                }
            }
            PdfValue? cleanMask = null;
            if (graph.Get(source, "SMask") is { } mask)
            {
                if (mask.Type != "stream" || graph.Number(mask, "Width") != width || graph.Number(mask, "Height") != height
                    || graph.Get(mask, "ColorSpace")?.Name != "DeviceGray")
                {
                    throw Unsupported("non-matching grayscale image soft mask");
                }
                cleanMask = Image(mask, isMask: true);
                if (graph.Get(mask, "Matte") is { } matte)
                {
                    if (matte.Items is not { } values || values.Count != channels
                        || values.Any(value => value.Type != "number" || !double.IsFinite(Number(value)) || Number(value) < 0 || Number(value) > 1))
                    {
                        throw Unsupported("invalid image matte components");
                    }
                    cleanMask.Entries!["Matte"] = CleanValue(matte);
                }
            }
            using var compressed = new MemoryStream();
            using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(pixels);
            }
            var image = Stream(compressed.ToArray());
            image.Entries!["Filter"] = Name("FlateDecode");
            image.Entries!["Type"] = Name("XObject");
            image.Entries["Subtype"] = Name("Image");
            image.Entries["Width"] = Num(width);
            image.Entries["Height"] = Num(height);
            image.Entries["BitsPerComponent"] = Num(8);
            image.Entries["ColorSpace"] = Name(color!);
            if (graph.Get(source, "Interpolate") is { } interpolate)
            {
                if (interpolate.Type != "boolean")
                {
                    throw Unsupported("invalid image interpolation");
                }
                image.Entries["Interpolate"] = new PdfValue { Type = "boolean", Boolean = interpolate.Boolean };
            }
            if (cleanMask is not null)
            {
                image.Entries["SMask"] = Add(cleanMask);
            }
            return image;
        }
        for (var pass = 0; pass < 2; pass++)
        {
            var discover = pass == 0;
            fontsReady = !discover;
            for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = pages[pageIndex];
                var occurrence = 0;
                var resourceNumber = 0;
                var inText = false;
                var operationCount = 0;
                var offset = 0;
                var state = new State(PdfMatrix.Identity);
                var stack = new Stack<State>();
                var path = new List<PdfOperation>();
                var points = new List<PdfImagePoint>();
                (string Content, PdfValue Resources) Rewrite(PdfValue source, PdfReference id, PdfValue resources, int? formOccurrence, int operationOffset, ref State current, Stack<State> saved, int depth)
                {
                    if (depth > 32)
                    {
                        throw Unsupported("Form nesting exceeds 32");
                    }
                    foreach (var device in new[] { "Gray", "RGB", "CMYK" })
                    {
                        if (graph.Get(resources, "ColorSpace")?.Entries?.GetValueOrDefault("Default" + device) is { } defaultSpace
                            && graph.Resolve(defaultSpace).Name != "Device" + device)
                        {
                            throw Unsupported("non-device Default" + device + " color space");
                        }
                    }
                    var output = new StringBuilder();
                    var fonts = Dict();
                    var xobjects = Dict();
                    var graphicsStates = Dict();
                    var operations = PdfContent.Parse(PdfStreamDecoder.Decode(source));
                    for (var index = 0; index < operations.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++operationCount > 200000)
                        {
                            throw Unsupported("200,000 operator page budget exceeded");
                        }
                        var op = operations[index];
                        var key = (id, index);
                        var a = op.Operands;
                        ValidateOperation(op);
                        if (path.Count > 0 && op.Operator is not ("m" or "l" or "c" or "v" or "y" or "h" or "re" or "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n" or "W" or "W*"))
                        {
                            throw Unsupported("interleaved path construction or clipping");
                        }
                        switch (op.Operator)
                        {
                            case "BT":
                                if (inText)
                                {
                                    throw Unsupported("nested text objects");
                                }
                                inText = true;
                                break;
                            case "ET":
                                if (!inText)
                                {
                                    throw Unsupported("unbalanced text objects");
                                }
                                inText = false;
                                break;
                            case "q":
                                saved.Push(current);
                                break;
                            case "Q":
                                current = saved.Count > 0 ? saved.Pop() : throw Unsupported("unbalanced graphics state");
                                break;
                            case "cm":
                                current = current with { Matrix = current.Matrix.Multiply(PdfMatrix.From(a)) };
                                break;
                            case "w":
                                current = current with { LineWidth = Math.Abs(Number(a[0])) };
                                break;
                            case "M":
                                current = current with { MiterLimit = Math.Max(1, Math.Abs(Number(a[0]))) };
                                break;
                            case "cs":
                            case "CS":
                                var colorSpace = DeviceColorSpace(a[0], resources);
                                current = op.Operator == "cs" ? current with { FillColorSpace = colorSpace } : current with { StrokeColorSpace = colorSpace };
                                output.Append('/').Append(colorSpace).Append(' ').Append(op.Operator).Append('\n');
                                continue;
                            case "sc":
                            case "scn":
                            case "SC":
                            case "SCN":
                                var selectedSpace = op.Operator is "sc" or "scn" ? current.FillColorSpace : current.StrokeColorSpace;
                                var channels = selectedSpace == "DeviceRGB" ? 3 : selectedSpace == "DeviceCMYK" ? 4 : 1;
                                if (a.Count != channels)
                                {
                                    throw Unsupported("color operands for " + selectedSpace);
                                }
                                break;
                            case "g": case "rg": case "k":
                                current = current with { FillColorSpace = op.Operator == "g" ? "DeviceGray" : op.Operator == "rg" ? "DeviceRGB" : "DeviceCMYK" };
                                break;
                            case "G": case "RG": case "K":
                                current = current with { StrokeColorSpace = op.Operator == "G" ? "DeviceGray" : op.Operator == "RG" ? "DeviceRGB" : "DeviceCMYK" };
                                break;
                            case "gs":
                                var sourceState = graph.Get(resources, "ExtGState")?.Entries?.GetValueOrDefault(a[0].Name!)
                                    ?? throw Unsupported("missing ExtGState /" + a[0].Name);
                                var cleanState = GraphicsState(sourceState);
                                current = current with
                                {
                                    LineWidth = cleanState.Entries!.TryGetValue("LW", out var lineWidth) ? Number(lineWidth) : current.LineWidth,
                                    MiterLimit = cleanState.Entries.TryGetValue("ML", out var miterLimit) ? Number(miterLimit) : current.MiterLimit,
                                    FillAlpha = cleanState.Entries.TryGetValue("ca", out var fillAlpha) ? Number(fillAlpha) : current.FillAlpha,
                                    StrokeAlpha = cleanState.Entries.TryGetValue("CA", out var strokeAlpha) ? Number(strokeAlpha) : current.StrokeAlpha
                                };
                                var stateName = "State" + resourceNumber++;
                                graphicsStates.Entries![stateName] = cleanState;
                                output.Append($"/{stateName} gs\n");
                                continue;
                            case "Tf":
                                var name = a[0].Name!;
                                var sourceFont = graph.Get(resources, "Font")?.Entries?.GetValueOrDefault(name) ?? throw Unsupported("missing font");
                                try
                                {
                                    fonts.Entries![name] = Font(sourceFont);
                                }
                                catch (NotSupportedException)
                                {
                                    // Numeric TJ advances do not depend on font glyphs. No source font
                                    // is copied unless some text using it survives the removal pass.
                                    fonts.Entries![name] = Dict(("Type", Name("Font")), ("Subtype", Name("Type1")), ("BaseFont", Name("Helvetica")));
                                }
                                break;
                            case "Tr":
                                if (Number(a[0]) != Math.Truncate(Number(a[0])) || Number(a[0]) is < 0 or > 3)
                                {
                                    throw Unsupported("text clipping");
                                }
                                current = current with { RenderingMode = (int)Number(a[0]) };
                                break;
                            case "Tj":
                            case "TJ":
                            case "'":
                            case "\"":
                                if (!inText)
                                {
                                    throw Unsupported("text outside a text object");
                                }
                                if (op.Operator == "\"")
                                {
                                    output.Append($"{Syntax(a[0])} Tw {Syntax(a[1])} Tc\n");
                                }
                                if (op.Operator is "'" or "\"")
                                {
                                    output.Append("T*\n");
                                }
                                var tokens = op.Operator == "TJ" ? a[0].Items! : [a.Last()];
                                var rewritten = new List<PdfValue>();
                                for (var token = 0; token < tokens.Count; token++)
                                {
                                    if (tokens[token].Type == "number")
                                    {
                                        rewritten.Add(tokens[token]);
                                        continue;
                                    }
                                    var slot = slotIndices[pageIndex].GetValueOrDefault((id, index + operationOffset, formOccurrence, op.Operator == "TJ" ? token : -1));
                                    if (slot is null || slot.FontSize <= 0 || slot.HorizontalScale <= 0)
                                    {
                                        throw Unsupported("unresolved text placement");
                                    }
                                    var fontBox = new double[] { -.4, -.5, 1.5, 1.5 };
                                    var metricFont = slot.Font.Source;
                                    if (graph.Get(metricFont, "DescendantFonts")?.Items is { Count: > 0 } descendants)
                                    {
                                        metricFont = graph.Resolve(descendants[0]);
                                    }
                                    var descriptor = graph.Get(metricFont, "FontDescriptor");
                                    if (descriptor is not null)
                                    {
                                        var bbox = graph.Get(descriptor, "FontBBox")?.Items;
                                        if (bbox is not { Count: 4 } || bbox.Any(value => value.Type != "number" || !double.IsFinite(Number(value)))
                                            || Number(bbox[2]) <= Number(bbox[0]) || Number(bbox[3]) <= Number(bbox[1]))
                                        {
                                            throw Unsupported("missing or invalid font bounds");
                                        }
                                        fontBox[0] = Math.Min(fontBox[0], Number(bbox[0]) / 1000);
                                        fontBox[1] = Math.Min(fontBox[1], Number(bbox[1]) / 1000);
                                        fontBox[2] = Math.Max(fontBox[2], Number(bbox[2]) / 1000);
                                        fontBox[3] = Math.Max(fontBox[3], Number(bbox[3]) / 1000);
                                    }
                                    else
                                    {
                                        // Only validated standard fonts have a built-in conservative box.
                                        Font(slot.Font.Source);
                                    }
                                    double advance = 0;
                                    for (var glyphIndex = 0; glyphIndex < slot.Glyphs.Count; glyphIndex++)
                                    {
                                        var glyph = slot.Glyphs[glyphIndex];
                                        var glyphKey = (id, key.Item2, slot.Token, glyphIndex);
                                        if (discover)
                                        {
                                            if (!fontCandidates.TryGetValue(slot.Font.Source, out var candidates))
                                            {
                                                candidates = [];
                                                fontCandidates.Add(slot.Font.Source, candidates);
                                            }
                                            candidates.Add((slot.Font, glyph, glyphKey));
                                        }
                                        var glyphBox = new PdfGlyphBounds(fontBox[0], fontBox[1], Math.Max(fontBox[2], glyph.Width + .4), fontBox[3]);
                                        var precise = outlineBounds.TryGetValue(slot.Font.Source, out var fontOutlines)
                                            && fontOutlines.TryGetValue(Convert.ToHexString(glyph.Bytes), out glyphBox);
                                        if (!precise)
                                        {
                                            precise = PdfStandardGlyphBounds.TryGet(graph, slot.Font, glyph, out glyphBox);
                                        }
                                        if (!precise)
                                        {
                                            glyphBox = new(fontBox[0], fontBox[1], Math.Max(fontBox[2], glyph.Width + .4), fontBox[3]);
                                        }
                                        else if (glyphBox.Left == glyphBox.Right || glyphBox.Bottom == glyphBox.Top)
                                        {
                                            // Spaces and other non-painting glyphs still have selectable text advances.
                                            glyphBox = new(0, -.2, Math.Max(0, glyph.Width), .8);
                                        }
                                        // Small font-space tolerance for rasterization; preserve actual overhangs.
                                        var tolerance = precise ? .02 : 0;
                                        var bounds = Corners(slot.TextMatrix, advance + (glyphBox.Left - tolerance) * slot.FontSize * slot.HorizontalScale,
                                            slot.Rise + (glyphBox.Bottom - tolerance) * slot.FontSize, advance + (glyphBox.Right + tolerance) * slot.FontSize * slot.HorizontalScale,
                                            slot.Rise + (glyphBox.Top + tolerance) * slot.FontSize);
                                        var textStrokePadding = current.RenderingMode is 1 or 2
                                            ? Math.Max(1, current.LineWidth) * current.MiterLimit * (Math.Abs(current.Matrix.A) + Math.Abs(current.Matrix.B) + Math.Abs(current.Matrix.C) + Math.Abs(current.Matrix.D)) : 0;
                                        var invisible = current.RenderingMode == 3
                                            || current.RenderingMode == 0 && current.FillAlpha == 0
                                            || current.RenderingMode == 1 && current.StrokeAlpha == 0
                                            || current.RenderingMode == 2 && current.FillAlpha == 0 && current.StrokeAlpha == 0;
                                        if (discover && (invisible || Intersects(pageIndex + 1, bounds, textStrokePadding)))
                                        {
                                            removedGlyphs.Add(glyphKey);
                                        }
                                        var spacing = slot.CharacterSpacing + (glyph.Bytes.Length == 1 && glyph.Bytes[0] == 32 ? slot.WordSpacing : 0);
                                        if (removedGlyphs.Contains(glyphKey))
                                        {
                                            if (!discover)
                                            {
                                                Report(pageIndex + 1, "text", bounds, textStrokePadding);
                                            }
                                            rewritten.Add(Num(-1000 * (glyph.Width + spacing / slot.FontSize)));
                                        }
                                        else
                                        {
                                            if (!discover)
                                            {
                                                try
                                                {
                                                    Font(slot.Font.Source);
                                                }
                                                catch (NotSupportedException exception)
                                                {
                                                    throw Unsupported($"remaining text on page {pageIndex + 1} using font '{slot.Font.Name}': {exception.Message}");
                                                }
                                            }
                                            var convertedSimple = graph.Get(slot.Font.Source, "Subtype")?.Name == "TrueType";
                                            rewritten.Add(Str(convertedSimple ? new byte[] { 0, glyph.Bytes[0] } : glyph.Bytes));
                                            // Tw applies to single-byte spaces, not the rebuilt two-byte CID codes.
                                            if (convertedSimple && glyph.Bytes[0] == 32 && slot.WordSpacing != 0)
                                            {
                                                rewritten.Add(Num(-1000 * slot.WordSpacing / slot.FontSize));
                                            }
                                        }
                                        advance += (glyph.Width * slot.FontSize + spacing) * slot.HorizontalScale;
                                    }
                                }
                                output.Append(Syntax(Array(rewritten.ToArray()))).Append(" TJ\n");
                                continue;
                            case "m":
                            case "l":
                            case "c":
                            case "v":
                            case "y":
                                for (var n = 0; n < a.Count; n += 2)
                                {
                                    points.Add(current.Matrix.Point(Number(a[n]), Number(a[n + 1])));
                                }
                                path.Add(op);
                                continue;
                            case "re":
                                points.AddRange(Corners(current.Matrix, Number(a[0]), Number(a[1]), Number(a[0]) + Number(a[2]), Number(a[1]) + Number(a[3])));
                                path.Add(op);
                                continue;
                            case "h":
                                path.Add(op);
                                continue;
                            case "W":
                            case "W*":
                                if (path.Count != 1 || path[0].Operator != "re" || index + 1 >= operations.Count || operations[index + 1].Operator != "n")
                                {
                                    throw Unsupported("non-rectangular clipping");
                                }
                                output.Append(path[0].Serialize()).Append(op.Serialize()).Append("n\n");
                                index++;
                                path.Clear();
                                points.Clear();
                                continue;
                            case "S":
                            case "s":
                            case "f":
                            case "F":
                            case "f*":
                            case "B":
                            case "B*":
                            case "b":
                            case "b*":
                            case "n":
                                var stroke = op.Operator is "S" or "s" or "B" or "B*" or "b" or "b*";
                                var padding = stroke ? Math.Max(1, current.LineWidth) * current.MiterLimit * (Math.Abs(current.Matrix.A) + Math.Abs(current.Matrix.B) + Math.Abs(current.Matrix.C) + Math.Abs(current.Matrix.D)) : 0;
                                if (discover && Intersects(pageIndex + 1, points, padding))
                                {
                                    removedPaths.Add(key);
                                }
                                if (!discover && op.Operator != "n" && removedPaths.Contains(key))
                                {
                                    Report(pageIndex + 1, "path", points, padding);
                                }
                                if (op.Operator != "n" && !removedPaths.Contains(key))
                                {
                                    foreach (var segment in path)
                                    {
                                        output.Append(segment.Serialize());
                                    }
                                    output.Append(op.Serialize());
                                }
                                path.Clear();
                                points.Clear();
                                continue;
                            case "Do":
                                if (inText)
                                {
                                    throw Unsupported("XObject inside a text object");
                                }
                                var original = graph.Get(resources, "XObject")?.Entries?.GetValueOrDefault(a[0].Name!) ?? throw Unsupported("missing XObject");
                                var obj = graph.Resolve(original);
                                var subtype = graph.Get(obj, "Subtype")?.Name;
                                if (subtype == "Image")
                                {
                                    if (discover && Intersects(pageIndex + 1, Corners(current.Matrix, 0, 0, 1, 1)))
                                    {
                                        removedImages.Add(obj);
                                    }
                                    if (!discover && removedImages.Contains(obj))
                                    {
                                        Report(pageIndex + 1, "image", Corners(current.Matrix, 0, 0, 1, 1));
                                    }
                                    if (!discover && !removedImages.Contains(obj))
                                    {
                                        var resourceName = "Image" + resourceNumber++;
                                        if (!retainedImages.TryGetValue(obj, out var retainedImage))
                                        {
                                            retainedImage = Add(Image(obj));
                                            retainedImages.Add(obj, retainedImage);
                                        }
                                        xobjects.Entries![resourceName] = retainedImage;
                                        output.Append($"/{resourceName} Do\n");
                                    }
                                }
                                else if (subtype == "Form")
                                {
                                    if (new[] { "OC", "Ref", "F" }.Any(item => graph.Get(obj, item) is not null) || original.Reference is null)
                                    {
                                        throw Unsupported("optional content or external Forms");
                                    }
                                    var group = TransparencyGroup(obj, graph.Get(obj, "Resources") ?? resources);
                                    var matrix = graph.Get(obj, "Matrix") ?? Array(Num(1), Num(0), Num(0), Num(1), Num(0), Num(0));
                                    var child = current with { Matrix = current.Matrix.Multiply(PdfMatrix.From(matrix.Items!)) };
                                    var childStack = new Stack<State>();
                                    var nested = Rewrite(obj, original.Reference, graph.Get(obj, "Resources") ?? resources, ++occurrence, 0, ref child, childStack, depth + 1);
                                    if (childStack.Count > 0 || path.Count > 0 || inText)
                                    {
                                        throw Unsupported("unbalanced Form content");
                                    }
                                    if (!discover)
                                    {
                                        var form = Stream(Encoding.Latin1.GetBytes(nested.Content));
                                        form.Entries!["Type"] = Name("XObject");
                                        form.Entries["Subtype"] = Name("Form");
                                        form.Entries["BBox"] = CleanValue(graph.Get(obj, "BBox") ?? throw Unsupported("missing Form bounds"));
                                        form.Entries["Matrix"] = CleanValue(matrix);
                                        form.Entries["Resources"] = nested.Resources;
                                        if (group is not null)
                                        {
                                            form.Entries["Group"] = group;
                                        }
                                        var resourceName = "Form" + resourceNumber++;
                                        xobjects.Entries![resourceName] = Add(form);
                                        output.Append($"/{resourceName} Do\n");
                                    }
                                }
                                else
                                {
                                    throw Unsupported("XObject subtype");
                                }
                                continue;
                            case "BMC":
                            case "BDC":
                            case "EMC":
                            case "MP":
                            case "DP":
                                if (a.FirstOrDefault()?.Name == "OC")
                                {
                                    throw Unsupported("optional-content visibility");
                                }
                                continue;
                            case "Tc": case "Tw": case "Tz": case "TL": case "Ts": case "Td": case "TD": case "Tm": case "T*":
                            case "J": case "j": case "d":
                                break;
                            default:
                                throw Unsupported("graphics operator " + op.Operator);
                        }
                        output.Append(op.Serialize());
                    }
                    return (output.ToString(), Dict(("Font", fonts), ("XObject", xobjects), ("ExtGState", graphicsStates)));
                }
                var allContent = new StringBuilder();
                var outputFonts = Dict();
                var outputObjects = Dict();
                var outputStates = Dict();
                foreach (var stream in page.Streams)
                {
                    var rewritten = Rewrite(stream.Value, stream.Id, page.Resources, null, offset, ref state, stack, 0);
                    offset += PdfContent.Parse(PdfStreamDecoder.Decode(stream.Value)).Count;
                    foreach (var entry in rewritten.Resources.Entries!["Font"].Entries!)
                    {
                        outputFonts.Entries![entry.Key] = entry.Value;
                    }
                    foreach (var entry in rewritten.Resources.Entries["XObject"].Entries!)
                    {
                        outputObjects.Entries![entry.Key] = entry.Value;
                    }
                    foreach (var entry in rewritten.Resources.Entries["ExtGState"].Entries!)
                    {
                        outputStates.Entries![entry.Key] = entry.Value;
                    }
                    allContent.Append(rewritten.Content);
                }
                if (stack.Count != 0 || path.Count != 0 || inText)
                {
                    throw Unsupported("unbalanced page content");
                }
                if (!discover)
                {
                    allContent.Insert(0, "q\n").Append("\nQ n\n");
                    foreach (var region in regions.Where(region => region.PageNumber == pageIndex + 1))
                    {
                        allContent.Append($"q 0 g {Syntax(Num(region.X))} {Syntax(Num(region.Y))} {Syntax(Num(region.Width))} {Syntax(Num(region.Height))} re f Q\n");
                    }
                    var target = Dict(("Type", Name("Page")), ("Parent", Ref(new(2))),
                        ("Contents", Add(Stream(Encoding.Latin1.GetBytes(allContent.ToString())))),
                        ("Resources", Dict(("Font", outputFonts), ("XObject", outputObjects), ("ExtGState", outputStates))));
                    foreach (var attribute in new[] { "MediaBox", "CropBox", "Rotate", "UserUnit" })
                    {
                        var node = page.Dictionary;
                        var seen = new HashSet<PdfValue>();
                        while (seen.Add(node))
                        {
                            if (graph.Get(node, attribute) is { } value)
                            {
                                target.Entries![attribute] = CleanValue(value);
                                break;
                            }
                            if (graph.Get(node, "Parent") is not { } parent)
                            {
                                break;
                            }
                            node = parent;
                        }
                    }
                    if (target.Entries!.GetValueOrDefault("MediaBox")?.Items is not { Count: 4 })
                    {
                        throw Unsupported("missing page bounds");
                    }
                    kids.Items!.Add(Add(target));
                }
            }
        }
        return result;
    }

    private static void ValidateOperation(PdfOperation operation)
    {
        var args = operation.Operands;
        var numericCount = operation.Operator switch
        {
            "cm" or "Tm" or "c" => 6,
            "v" or "y" or "re" or "k" or "K" => 4,
            "rg" or "RG" => 3,
            "m" or "l" or "Td" or "TD" => 2,
            "w" or "M" or "Tr" or "Tc" or "Tw" or "Tz" or "TL" or "Ts" or "g" or "G" or "J" or "j" => 1,
            "q" or "Q" or "BT" or "ET" or "T*" or "h" or "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n" or "W" or "W*" or "EMC" => 0,
            _ => -1
        };
        if (numericCount >= 0)
        {
            if (args.Count != numericCount || args.Any(value => value.Type != "number" || !double.IsFinite(Number(value))))
            {
                throw Unsupported("invalid operands for " + operation.Operator);
            }
            return;
        }
        var valid = operation.Operator switch
        {
            "sc" or "scn" or "SC" or "SCN" => args.Count is >= 1 and <= 4 && args.All(value => value.Type == "number" && double.IsFinite(Number(value))),
            "Tf" => args.Count == 2 && args[0].Type == "name" && args[1].Type == "number" && double.IsFinite(Number(args[1])),
            "Do" or "gs" or "cs" or "CS" or "BMC" or "MP" => args.Count == 1 && args[0].Type == "name",
            "BDC" or "DP" => args.Count == 2 && args[0].Type == "name" && args[1].Type is "dictionary" or "name",
            "Tj" or "'" => args.Count == 1 && args[0].Type == "string",
            "\"" => args.Count == 3 && args[0].Type == "number" && args[1].Type == "number" && double.IsFinite(Number(args[0])) && double.IsFinite(Number(args[1])) && args[2].Type == "string",
            "TJ" => args.Count == 1 && args[0].Items is { } items && items.All(value => value.Type == "string" || value.Type == "number" && double.IsFinite(Number(value))),
            "d" => args.Count == 2 && args[0].Items is { } dash && dash.All(value => value.Type == "number" && double.IsFinite(Number(value))) && args[1].Type == "number" && double.IsFinite(Number(args[1])),
            _ => false
        };
        if (!valid)
        {
            throw Unsupported("invalid or unsupported operator " + operation.Operator);
        }
    }

    private static NotSupportedException Unsupported(string detail) => new($"Content-preserving redaction cannot safely process {detail}. Use image-only redaction instead.");
}
