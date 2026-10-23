using PdfObjectKit.Graphs;
using PdfObjectKit.Primitives;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfObjectKit.Fonts;

internal sealed class PdfNativeFont
{
    private readonly Dictionary<string, string> unicode = [];
    private readonly Dictionary<int, double> widths = [];
    private readonly int codeLength;
    private readonly double defaultWidth;
    private readonly bool requireExplicitWidth;
    public string Name { get; }

    public bool IsVertical { get; }

    internal PdfValue Source { get; }

    internal Dictionary<string, string> EncodingUnicode { get; } = [];

    private static readonly int[] HelveticaWidths = [278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584];
    public PdfNativeFont(PdfGraph graph, PdfValue font)
    {
        font = graph.Resolve(font);
        Source = font;
        Name = graph.Get(font, "BaseFont")?.Name ?? "Unknown";
        var subtype = graph.Get(font, "Subtype")?.Name;
        if (subtype is not ("Type0" or "Type1" or "TrueType"))
        {
            throw new NotSupportedException($"Unsupported font subtype {subtype}.");
        }

        var composite = subtype == "Type0";
        var encoding = graph.Get(font, "Encoding");
        IsVertical = encoding?.Name?.EndsWith("-V", StringComparison.Ordinal) == true;
        codeLength = composite ? 2 : 1;
        if (composite && encoding?.Name is not ("Identity-H" or "Identity-V"))
        {
            throw new NotSupportedException("Only Identity-H/V composite font encodings are supported.");
        }

        var metricFont = composite ? graph.Resolve(graph.Get(font, "DescendantFonts")!.Items![0]) : font;
        defaultWidth = composite ? graph.Number(metricFont, "DW", 1000) / 1000 : 0;
        if (composite && graph.Get(metricFont, "W")?.Items is { } w)
        {
            for (var i = 0; i < w.Count;)
            {
                var first = checked((int)PdfPrimitives.Number(w[i++]));
                var next = graph.Resolve(w[i++]);
                if (next.Items is { } values)
                {
                    foreach (var value in values)
                    {
                        widths[first++] = PdfPrimitives.Number(value) / 1000;
                    }
                }
                else
                {
                    var last = checked((int)PdfPrimitives.Number(next));
                    var width = PdfPrimitives.Number(w[i++]) / 1000;
                    if (last < first || last - first > 65535)
                    {
                        throw new InvalidDataException("Invalid CID width range.");
                    }

                    for (; first <= last; first++)
                    {
                        widths[first] = width;
                    }
                }
            }
        }

        if (!composite)
        {
            if (graph.Get(font, "Widths")?.Items is { } values)
            {
                var first = (int)graph.Number(font, "FirstChar");
                foreach (var value in values)
                {
                    widths[first++] = PdfPrimitives.Number(value) / 1000;
                }

                var descriptor = graph.Get(font, "FontDescriptor");
                defaultWidth = descriptor is null ? 0 : graph.Number(descriptor, "MissingWidth") / 1000;
            }
            else if (Name == "Helvetica")
            {
                requireExplicitWidth = true;
                for (var i = 0; i < HelveticaWidths.Length; i++)
                {
                    widths[i + 32] = HelveticaWidths[i] / 1000d;
                }
            }
            else if (Name.StartsWith("Courier", StringComparison.Ordinal))
            {
                defaultWidth = .6;
            }
            else
            {
                throw new NotSupportedException($"Font '{Name}' has no supported width metrics.");
            }

            var baseEncoding = encoding?.Name ?? (encoding is null ? null : graph.Get(encoding, "BaseEncoding")?.Name);
            if (baseEncoding is not (null or "WinAnsiEncoding" or "StandardEncoding"))
            {
                throw new NotSupportedException($"Unsupported simple font encoding {baseEncoding}.");
            }

            // StandardEncoding's ASCII quotes differ from WinAnsi.
            for (var i = 32; i < 127; i++)
            {
                unicode[i.ToString("X2")] = baseEncoding != "WinAnsiEncoding" && i == 39 ? "\u2019" : baseEncoding != "WinAnsiEncoding" && i == 96 ? "\u2018" : ((char)i).ToString();
            }

            if (baseEncoding == "WinAnsiEncoding")
            {
                const string special = "\u20ac\0\u201a\u0192\u201e\u2026\u2020\u2021\u02c6\u2030\u0160\u2039\u0152\0\u017d\0\0\u2018\u2019\u201c\u201d\u2022\u2013\u2014\u02dc\u2122\u0161\u203a\u0153\0\u017e\u0178";
                for (var i = 128; i < 256; i++)
                {
                    var ch = i < 160 ? special[i - 128] : (char)i;
                    if (ch != 0)
                    {
                        unicode[i.ToString("X2")] = ch.ToString();
                    }
                }
            }

            if (encoding?.Entries is not null && graph.Get(encoding, "Differences")?.Items is { } differences)
            {
                if (requireExplicitWidth)
                {
                    throw new NotSupportedException("Encoding Differences require explicit font Widths.");
                }

                var code = 0;
                foreach (var item in differences)
                {
                    if (item.Number is not null)
                    {
                        code = (int)PdfPrimitives.Number(item);
                    }
                    else
                    {
                        unicode.Remove(code.ToString("X2"));
                        var text = GlyphName(item.Name!);
                        if (text is not null)
                        {
                            unicode[code.ToString("X2")] = text;
                        }

                        code++;
                    }
                }
            }
        }

        foreach (var pair in unicode)
        {
            EncodingUnicode[pair.Key] = pair.Value;
        }
        if (graph.Get(font, "ToUnicode")is { } cmap)
        {
            ReadCMap(PdfStreamDecoder.Decode(cmap));
        }
        else if (composite)
        {
            throw new NotSupportedException("Composite font requires a ToUnicode map.");
        }
    }

    private static string? GlyphName(string name)
    {
        if (name.Length == 1)
        {
            return name;
        }

        if (name.StartsWith("uni") && name.Length == 7 && int.TryParse(name[3..], NumberStyles.HexNumber, null, out var code))
        {
            return ((char)code).ToString();
        }

        if (name.StartsWith('u') && int.TryParse(name[1..], NumberStyles.HexNumber, null, out code) && Rune.IsValid(code))
        {
            return char.ConvertFromUtf32(code);
        }

        var names = new Dictionary<string, string>
        {
            ["space"] = " ",
            ["period"] = ".",
            ["comma"] = ",",
            ["hyphen"] = "-",
            ["colon"] = ":",
            ["semicolon"] = ";",
            ["parenleft"] = "(",
            ["parenright"] = ")",
            ["slash"] = "/",
            ["quotedbl"] = "\"",
            ["quotesingle"] = "'",
            ["ampersand"] = "&",
            ["exclam"] = "!",
            ["question"] = "?",
            ["zero"] = "0",
            ["one"] = "1",
            ["two"] = "2",
            ["three"] = "3",
            ["four"] = "4",
            ["five"] = "5",
            ["six"] = "6",
            ["seven"] = "7",
            ["eight"] = "8",
            ["nine"] = "9",
            ["fi"] = "fi",
            ["fl"] = "fl"
        };
        return names.GetValueOrDefault(name);
    }

    private void ReadCMap(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        if (Regex.IsMatch(text, @"\busecmap\b"))
        {
            throw new NotSupportedException("Inherited Unicode CMaps are not supported.");
        }

        foreach (Match block in Regex.Matches(text, @"beginbf(char|range)\b(.*?)endbf\1", RegexOptions.Singleline))
        {
            var reader = new PdfSyntaxReader(Encoding.Latin1.GetBytes(block.Groups[2].Value));
            while (true)
            {
                reader.Skip();
                if (reader.Position == reader.Bytes.Length)
                {
                    break;
                }

                var first = Convert.FromBase64String(reader.Value().Base64!);
                if (first.Length != codeLength)
                {
                    throw new NotSupportedException("Unexpected ToUnicode source code length.");
                }

                var start = Convert.ToInt32(Convert.ToHexString(first), 16);
                if (block.Groups[1].Value == "char")
                {
                    Set(first, Convert.FromBase64String(reader.Value().Base64!));
                }
                else
                {
                    var endBytes = Convert.FromBase64String(reader.Value().Base64!);
                    var end = Convert.ToInt32(Convert.ToHexString(endBytes), 16);
                    if (end < start || end - start > 65535)
                    {
                        throw new InvalidDataException("Invalid Unicode CMap range.");
                    }

                    var dest = reader.Value();
                    var baseBytes = dest.Base64 is null ? null : Convert.FromBase64String(dest.Base64);
                    for (var code = start; code <= end; code++)
                    {
                        var target = dest.Items is { } items ? Convert.FromBase64String(items[code - start].Base64!) : baseBytes!.ToArray();
                        if (baseBytes is not null)
                        {
                            var increment = code - start;
                            for (var p = target.Length - 1; p >= 0 && increment != 0; p--)
                            {
                                increment += target[p];
                                target[p] = (byte)increment;
                                increment >>= 8;
                            }
                        }

                        Set(codeLength == 1 ? [(byte)code] : [(byte)(code >> 8), (byte)code], target);
                    }
                }
            }
        }

        void Set(byte[] source, byte[] dest)
        {
            if (dest.Length == 0 || dest.Length % 2 != 0)
            {
                throw new InvalidDataException("Invalid UTF-16 ToUnicode mapping.");
            }

            unicode[Convert.ToHexString(source)] = new UnicodeEncoding(true, false, true).GetString(dest);
        }
    }

    public List<NativeGlyph> Decode(byte[] bytes)
    {
        if (bytes.Length % codeLength != 0)
        {
            throw new InvalidDataException("Truncated font code.");
        }

        var result = new List<NativeGlyph>();
        for (var i = 0; i < bytes.Length; i += codeLength)
        {
            var data = bytes.AsSpan(i, codeLength).ToArray();
            if (!unicode.TryGetValue(Convert.ToHexString(data), out var text))
            {
                throw new InvalidDataException("Font code has no Unicode mapping.");
            }

            var code = codeLength == 1 ? data[0] : data[0] * 256 + data[1];
            if (requireExplicitWidth && !widths.ContainsKey(code))
            {
                throw new NotSupportedException("No built-in metric for this font code; explicit Widths are required.");
            }

            result.Add(new(text, data, widths.GetValueOrDefault(code, defaultWidth)));
        }

        return result;
    }

    public IEnumerable<NativeGlyph> Alphabet() => unicode.Keys.Where(key => !requireExplicitWidth || widths.ContainsKey(Convert.ToInt32(key, 16))).Select(key => Decode(Convert.FromHexString(key))[0]);
}
