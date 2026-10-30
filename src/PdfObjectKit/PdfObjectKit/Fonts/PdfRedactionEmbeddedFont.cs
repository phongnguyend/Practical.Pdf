using System.Text;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Rendering;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Fonts;

internal static class PdfRedactionEmbeddedFont
{
    public static PdfValue Build(PdfGraph graph, PdfNativeFont font, IReadOnlyList<NativeGlyph> glyphs, Func<PdfValue, PdfValue> add)
    {
        try
        {
            return BuildCore(graph, font, glyphs, add);
        }
        catch (Exception exception) when (exception is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("Malformed embedded TrueType font cannot be redaction-subsetted.", exception);
        }
    }

    private static PdfValue BuildCore(PdfGraph graph, PdfNativeFont font, IReadOnlyList<NativeGlyph> glyphs, Func<PdfValue, PdfValue> add)
    {
        var source = font.Source;
        var simple = graph.Get(source, "Subtype")?.Name == "TrueType";
        if (!simple && (graph.Get(source, "Subtype")?.Name != "Type0" || graph.Get(source, "Encoding")?.Name != "Identity-H"
            || graph.Get(source, "ToUnicode") is null))
        {
            throw new NotSupportedException("Redaction font rebuilding requires simple TrueType or horizontal Identity-H TrueType with resolved Unicode mappings.");
        }
        var unique = glyphs.DistinctBy(glyph => Convert.ToHexString(glyph.Bytes)).ToArray();
        if (unique.Length == 0 || unique.Any(glyph => glyph.Bytes.Length != (simple ? 1 : 2) || string.IsNullOrEmpty(glyph.Text)
            || glyph.Text.Contains('\uFFFD') || !double.IsFinite(glyph.Width)))
        {
            throw new NotSupportedException("Unresolved retained font glyphs.");
        }
        var rebuilt = new PdfCanvasFontBuilder(graph, font, unique, redactionSubset: true);
        var bytes = Convert.FromBase64String(rebuilt.Font.Data);
        var parsed = new PdfTrueTypeFont(bytes);
        var maxCode = unique.Max(glyph => Convert.ToInt32(Convert.ToHexString(glyph.Bytes), 16));
        var map = new byte[(maxCode + 1) * 2];
        var widths = new List<PdfValue>();
        var unicode = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /RedactedUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var chunk in unique.Chunk(100))
        {
            unicode.Append(chunk.Length).Append(" beginbfchar\n");
            foreach (var glyph in chunk)
            {
                var code = Convert.ToInt32(Convert.ToHexString(glyph.Bytes), 16);
                var key = Convert.ToHexString(glyph.Bytes);
                var gid = parsed.Glyph(rebuilt.Characters[key].EnumerateRunes().Single().Value);
                map[code * 2] = (byte)(gid >> 8);
                map[code * 2 + 1] = (byte)gid;
                widths.Add(Num(code));
                widths.Add(Array(Num(glyph.Width * 1000)));
                unicode.Append('<').Append(code.ToString("X4")).Append("> <").Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(glyph.Text))).Append(">\n");
            }
            unicode.Append("endbfchar\n");
        }
        unicode.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        var metric = simple ? source : graph.Resolve(graph.Get(source, "DescendantFonts")!.Items![0]);
        var original = graph.Get(metric, "FontDescriptor")!;
        var descriptor = Dict(("Type", Name("FontDescriptor")), ("FontName", Name("PdfRedactedFont")),
            ("Flags", Num(32)), ("FontFile2", add(Stream(bytes))));
        foreach (var key in new[] { "Ascent", "Descent", "CapHeight", "ItalicAngle", "StemV" })
        {
            var value = graph.Get(original, key);
            if (value?.Type != "number" || !double.IsFinite(Number(value)))
            {
                throw new NotSupportedException("Missing or invalid font descriptor /" + key);
            }
            descriptor.Entries![key] = Num(Number(value));
        }
        var box = graph.Get(original, "FontBBox")?.Items;
        if (box is not { Count: 4 } || box.Any(value => value.Type != "number" || !double.IsFinite(Number(value))))
        {
            throw new NotSupportedException("Invalid font bounding box.");
        }
        descriptor.Entries!["FontBBox"] = Array(box.Select(value => Num(Number(value))).ToArray());
        var cid = Dict(("Type", Name("Font")), ("Subtype", Name("CIDFontType2")), ("BaseFont", Name("PdfRedactedFont")),
            ("CIDSystemInfo", Dict(("Registry", Str("Adobe"u8.ToArray())), ("Ordering", Str("Identity"u8.ToArray())), ("Supplement", Num(0)))),
            ("FontDescriptor", add(descriptor)), ("CIDToGIDMap", add(Stream(map))), ("W", Array(widths.ToArray())));
        return Dict(("Type", Name("Font")), ("Subtype", Name("Type0")), ("BaseFont", Name("PdfRedactedFont")),
            ("Encoding", Name("Identity-H")), ("DescendantFonts", Array(add(cid))),
            ("ToUnicode", add(Stream(Encoding.ASCII.GetBytes(unicode.ToString())))));
    }
}
