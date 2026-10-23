using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using PdfObjectKit.Fonts;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Rendering;

/// <summary>Experimental display list with embedded TrueType fonts where supported.</summary>
public static class PdfCanvasExporter
{
    public static PdfCanvasPage Export(PdfObjectDocument document, int pageNumber)
    {
        var graph = new PdfGraph(document);
        var pages = graph.Pages();
        if (pageNumber < 1 || pageNumber > pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }
        var page = pages[pageNumber - 1];
        var interpreted = new PdfPageInterpreter(graph, page);
        var result = new PdfCanvasPage();
        var warnings = new HashSet<string> { "Experimental: annotations and advanced PDF graphics may differ from PDF.js." };
        if (interpreted.TextWarning is { } textWarning)
        {
            warnings.Add(textWarning);
        }
        var slots = interpreted.Slots.ToLookup(slot => (slot.Stream, slot.FormOccurrenceId, slot.Operation));
        var fonts = new Dictionary<PdfNativeFont, PdfCanvasFontBuilder>();
        var fontBytes = 0L;
        foreach (var group in interpreted.Slots.GroupBy(slot => slot.Font))
        {
            try
            {
                var font = new PdfCanvasFontBuilder(graph, group.Key, group.SelectMany(slot => slot.Glyphs));
                fontBytes += font.Font.Data.Length;
                if (fontBytes > 64 * 1024 * 1024)
                {
                    throw new NotSupportedException("Page font-data budget exceeded.");
                }
                fonts.Add(group.Key, font);
                if (!result.Fonts.Any(item => item.Id == font.Font.Id))
                {
                    result.Fonts.Add(font.Font);
                }
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or ArgumentException or OverflowException)
            {
                warnings.Add($"Font '{group.Key.Name}' uses a browser substitute: {ex.Message}");
            }
        }
        var forms = 0;
        var decodedPixels = 0L;
        var imageCache = new Dictionary<PdfValue, PdfCanvasCommand>();
        void Add(PdfCanvasCommand command)
        {
            if (result.Commands.Count >= 200000)
            {
                throw new NotSupportedException("Experimental renderer display-list limit exceeded.");
            }
            result.Commands.Add(command);
        }
        PdfCanvasCommand Image(PdfValue image)
        {
            if (imageCache.TryGetValue(image, out var cached))
            {
                return cached;
            }
            var width = checked((int)graph.Number(image, "Width"));
            var height = checked((int)graph.Number(image, "Height"));
            decodedPixels += (long)width * height;
            if (width <= 0 || height <= 0 || decodedPixels > 16000000)
            {
                throw new NotSupportedException("Image pixel budget exceeded.");
            }
            if (graph.Get(image, "ImageMask")?.Boolean == true || graph.Get(image, "Mask") is not null || graph.Get(image, "Decode") is not null)
            {
                throw new NotSupportedException("Image masks and custom Decode arrays are not supported.");
            }
            var filter = graph.Get(image, "Filter");
            var color = graph.Get(image, "ColorSpace")?.Name;
            byte[]? alpha = null;
            double[]? matte = null;
            if (graph.Get(image, "SMask") is { } mask)
            {
                if (graph.Number(mask, "Width") != width || graph.Number(mask, "Height") != height || graph.Number(mask, "BitsPerComponent") != 8 || graph.Get(mask, "ColorSpace")?.Name != "DeviceGray")
                {
                    throw new NotSupportedException("Only matching-size 8-bit grayscale soft masks are supported.");
                }
                alpha = PdfStreamDecoder.Decode(mask);
                if (alpha.Length < checked(width * height))
                {
                    throw new InvalidDataException("Truncated alpha samples.");
                }
                if (graph.Get(mask, "Decode")?.Items is { } decode)
                {
                    if (decode.Count != 2)
                    {
                        throw new InvalidDataException("Invalid soft-mask Decode array.");
                    }
                    var low = Number(decode[0]);
                    var high = Number(decode[1]);
                    for (var pixel = 0; pixel < width * height; pixel++)
                    {
                        alpha[pixel] = (byte)Math.Clamp(Math.Round(255 * low + alpha[pixel] * (high - low)), 0, 255);
                    }
                }
                if (graph.Get(mask, "Matte")?.Items is { } components)
                {
                    var channels = color == "DeviceRGB" ? 3 : color == "DeviceGray" ? 1 : 0;
                    if (channels == 0 || components.Count != channels)
                    {
                        throw new NotSupportedException("Unsupported soft-mask matte color space.");
                    }
                    matte = channels == 1 ? Enumerable.Repeat(Number(components[0]), 3).ToArray() : components.Select(Number).ToArray();
                }
            }
            PdfCanvasCommand command;
            if ((filter?.Name == "DCTDecode" || filter?.Items is { Count: 1 } filters && graph.Resolve(filters[0]).Name == "DCTDecode") && color is "DeviceRGB" or "DeviceGray")
            {
                command = new("jpeg") { Data = image.Base64, Width = width, Height = height, AlphaData = alpha is null ? null : Convert.ToBase64String(alpha), Matte = matte };
            }
            else
            {
                var channels = color == "DeviceRGB" ? 3 : color == "DeviceGray" ? 1 : 0;
                if (channels == 0 || graph.Number(image, "BitsPerComponent") != 8)
                {
                    throw new NotSupportedException("Only 8-bit DeviceRGB/DeviceGray images are supported.");
                }
                var bytes = PdfStreamDecoder.Decode(image);
                if (bytes.Length < (long)width * height * channels)
                {
                    throw new InvalidDataException("Truncated image samples.");
                }
                var rgba = new byte[checked(width * height * 4)];
                for (var pixel = 0; pixel < width * height; pixel++)
                {
                    rgba[pixel * 4] = bytes[pixel * channels];
                    rgba[pixel * 4 + 1] = bytes[pixel * channels + (channels == 3 ? 1 : 0)];
                    rgba[pixel * 4 + 2] = bytes[pixel * channels + (channels == 3 ? 2 : 0)];
                    var opacity = alpha?[pixel] ?? 255;
                    rgba[pixel * 4 + 3] = opacity;
                    if (matte is not null && opacity > 0)
                    {
                        for (var channel = 0; channel < 3; channel++)
                        {
                            rgba[pixel * 4 + channel] = (byte)Math.Clamp(Math.Round((rgba[pixel * 4 + channel] - (255 - opacity) * matte[channel]) * 255 / opacity), 0, 255);
                        }
                    }
                }
                command = new("rgba") { Data = Convert.ToBase64String(rgba), Width = width, Height = height };
            }
            imageCache[image] = command;
            return command;
        }
        void Walk(PdfValue source, PdfReference id, PdfValue resources, int? occurrence, int offset, int depth)
        {
            if (depth > 100)
            {
                throw new NotSupportedException("Form nesting exceeds limit.");
            }
            var operations = PdfContent.Parse(PdfStreamDecoder.Decode(source));
            for (var i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                var args = op.Operands;
                if (op.Operator is "Tj" or "TJ" or "'" or "\"")
                {
                    foreach (var slot in slots[(id, occurrence, offset + i)])
                    {
                        var m = slot.TextMatrix;
                        fonts.TryGetValue(slot.Font, out var font);
                        Add(new("text")
                        {
                            Numbers = [m.A, m.B, m.C, m.D, m.E, m.F, slot.FontSize, slot.HorizontalScale, slot.Rise],
                            FontName = slot.Font.Name,
                            FontId = font?.Font.Id,
                            Glyphs = slot.Glyphs.Select(glyph => new PdfCanvasGlyph(glyph.Text, glyph.Width * slot.FontSize,
                                glyph.Width * slot.FontSize + slot.CharacterSpacing + (glyph.Bytes.Length == 1 && glyph.Bytes[0] == 32 ? slot.WordSpacing : 0))
                            {
                                RenderText = font?.Characters[Convert.ToHexString(glyph.Bytes)]
                            }).ToList()
                        });
                    }
                }
                else if (op.Operator is "Do" or "BI")
                {
                    var raw = op.InlineImage ?? graph.Get(resources, "XObject")?.Entries?.GetValueOrDefault(args[0].Name!) ?? throw new InvalidDataException("Missing XObject.");
                    var target = graph.Resolve(raw);
                    if (graph.Get(target, "Subtype")?.Name == "Form")
                    {
                        if (graph.Get(target, "Group") is not null)
                        {
                            warnings.Add("Form transparency groups are approximated.");
                        }
                        Add(new("q"));
                        if (graph.Get(target, "Matrix")?.Items is { } matrix)
                        {
                            Add(new("cm") { Numbers = matrix.Select(Number).ToArray() });
                        }
                        if (graph.Get(target, "BBox")?.Items is { Count: 4 } box)
                        {
                            var b = box.Select(Number).ToArray();
                            Add(new("formClip") { Numbers = [b[0], b[1], b[2] - b[0], b[3] - b[1]] });
                        }
                        Walk(target, raw.Reference!, graph.Get(target, "Resources") ?? resources, ++forms, 0, depth + 1);
                        Add(new("Q"));
                    }
                    else
                    {
                        try
                        {
                            Add(Image(target));
                        }
                        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
                        {
                            warnings.Add(ex.Message);
                        }
                    }
                }
                else if (op.Operator is "cs" or "CS")
                {
                    if (args[0].Name is not ("Pattern" or "DeviceRGB" or "DeviceGray" or "DeviceCMYK"))
                    {
                        warnings.Add($"Unsupported color space: {args[0].Name}.");
                    }
                }
                else if (op.Operator is "scn" or "SCN" or "sc" or "SC")
                {
                    if (args.LastOrDefault()?.Name is { } patternName)
                    {
                        try
                        {
                            var pattern = graph.Get(resources, "Pattern")?.Entries?.GetValueOrDefault(patternName) ?? throw new InvalidDataException("Missing color pattern.");
                            Add(PdfCanvasGradient.Create(graph, graph.Resolve(pattern), char.IsUpper(op.Operator[0])));
                        }
                        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
                        {
                            warnings.Add(ex.Message);
                        }
                    }
                    else if (args.Count is 1 or 3 or 4)
                    {
                        Add(new(char.IsUpper(op.Operator[0]) ? "RG" : "rg") { Numbers = args.Select(Number).ToArray() });
                    }
                }
                else if (op.Operator == "gs")
                {
                    var state = graph.Get(resources, "ExtGState")?.Entries?.GetValueOrDefault(args[0].Name!);
                    if (state is not null)
                    {
                        Add(new("alpha") { Numbers = [graph.Number(state, "ca", -1), graph.Number(state, "CA", -1)] });
                        var softMask = graph.Get(state, "SMask");
                        if (softMask is not null && softMask.Name != "None")
                        {
                            warnings.Add("Graphics-state soft masks are not supported.");
                        }
                        var blend = graph.Get(state, "BM");
                        var modes = blend?.Items?.Select(item => graph.Resolve(item).Name).ToArray()
                            ?? (blend is null ? [] : new[] { blend.Name });
                        if (modes.Any(mode => mode is not ("Normal" or "Compatible")))
                        {
                            warnings.Add($"Unsupported graphics-state blend mode: {string.Join(", ", modes.Select(mode => mode ?? "invalid"))}.");
                        }
                    }
                }
                else if (op.Operator == "d")
                {
                    Add(new("d") { Numbers = [Number(args[1]), .. args[0].Items!.Select(Number)] });
                }
                else if (op.Operator is "q" or "Q" or "cm" or "m" or "l" or "c" or "v" or "y" or "h" or "re" or "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n" or "W" or "W*" or "w" or "J" or "j" or "M" or "g" or "G" or "rg" or "RG" or "k" or "K" or "Tr")
                {
                    Add(new(op.Operator) { Numbers = args.Select(Number).ToArray() });
                }
                else if (op.Operator is not ("BT" or "ET" or "Tf" or "Tc" or "Tw" or "Tz" or "TL" or "Ts" or "Tm" or "Td" or "TD" or "T*" or "BMC" or "BDC" or "EMC" or "MP" or "DP"))
                {
                    warnings.Add($"Unsupported graphics operator: {op.Operator}.");
                }
            }
        }
        var offset = 0;
        foreach (var stream in page.Streams)
        {
            Walk(stream.Value, stream.Id, page.Resources, null, offset, 0);
            offset += PdfContent.Parse(PdfStreamDecoder.Decode(stream.Value)).Count;
        }
        result.Warnings.AddRange(warnings);
        return result;
    }
}

public sealed class PdfCanvasPage
{
    public List<PdfCanvasFont> Fonts { get; } = [];

    public List<PdfCanvasCommand> Commands { get; } = [];

    public List<string> Warnings { get; } = [];
}

public sealed record PdfCanvasFont(string Id, string Name, string Data);

public sealed record PdfCanvasGlyph(string Text, double Width, double Advance)
{
    public string? RenderText { get; init; }
}

public sealed class PdfCanvasCommand(string kind)
{
    public string Kind { get; } = kind;

    public double[] Numbers { get; init; } = [];

    public string? FontName { get; init; }

    public string? FontId { get; init; }

    public List<PdfCanvasGlyph>? Glyphs { get; init; }

    public string? Data { get; init; }

    /// <summary>Grayscale soft-mask samples for browser-decoded JPEG images.</summary>
    public string? AlphaData { get; init; }

    /// <summary>RGB matte components to unblend before applying the soft mask.</summary>
    public double[]? Matte { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}
