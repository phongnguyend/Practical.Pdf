using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PdfObjectKit.Fonts;
using PdfObjectKit.Graphs;
using PdfObjectKit.Parsers;

namespace PdfObjectKit.Rendering;

// Rebuild the browser cmap using private-use characters. Rendering glyph identity is
// deliberately independent of ToUnicode, which describes extraction, not appearance.
internal sealed class PdfCanvasFontBuilder
{
    private readonly Dictionary<string, byte[]> tables = [];
    private static int U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    private static int U32(byte[] data, int offset) => checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)));
    private static void W16(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), checked((ushort)value));
    private static void W32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);

    public PdfCanvasFont Font { get; }

    public Dictionary<string, string> Characters { get; } = [];

    public Dictionary<string, PdfGlyphBounds> GlyphBounds { get; } = [];

    public PdfCanvasFontBuilder(PdfGraph graph, PdfNativeFont font, IEnumerable<NativeGlyph> glyphs, bool redactionSubset = false)
    {
        var source = font.Source;
        var composite = graph.Get(source, "Subtype")?.Name == "Type0";
        var metric = composite ? graph.Resolve(graph.Get(source, "DescendantFonts")!.Items![0]) : source;
        if (composite && graph.Get(metric, "Subtype")?.Name != "CIDFontType2")
        {
            throw new NotSupportedException("Only TrueType CID fonts are supported.");
        }
        var descriptor = graph.Get(metric, "FontDescriptor");
        var file = descriptor is null ? null : graph.Get(descriptor, "FontFile2");
        if (file is null)
        {
            throw new NotSupportedException("No embedded TrueType FontFile2.");
        }
        var bytes = PdfStreamDecoder.Decode(file);
        if (bytes.Length > 16 * 1024 * 1024 || U32(bytes, 0) != 0x10000)
        {
            throw new NotSupportedException("Expected an embedded glyf TrueType font up to 16 MB.");
        }
        var count = U16(bytes, 4);
        if (count == 0 || count > 256)
        {
            throw new InvalidDataException("Invalid TrueType table count.");
        }
        for (var i = 0; i < count; i++)
        {
            var record = 12 + i * 16;
            var tag = Encoding.ASCII.GetString(bytes.AsSpan(record, 4));
            var offset = U32(bytes, record + 8);
            var length = U32(bytes, record + 12);
            if (!tables.TryAdd(tag, bytes.AsSpan(offset, length).ToArray()))
            {
                throw new InvalidDataException("Duplicate TrueType table.");
            }
        }
        foreach (var tag in new[] { "head", "hhea", "hmtx", "maxp", "glyf", "loca", "name" })
        {
            if (!tables.ContainsKey(tag))
            {
                throw new NotSupportedException($"Missing TrueType {tag} table.");
            }
        }
        var glyphCount = U16(tables["maxp"], 4);
        var cidMap = graph.Get(metric, "CIDToGIDMap");
        var cidBytes = cidMap?.Type == "stream" ? PdfStreamDecoder.Decode(cidMap) : null;
        if (composite && cidMap is not null && cidBytes is null && cidMap.Name != "Identity")
        {
            throw new NotSupportedException("Unsupported CIDToGIDMap.");
        }
        var symbolic = descriptor is not null && ((int)graph.Number(descriptor, "Flags") & 4) != 0;
        var mapping = new Dictionary<int, int>();
        foreach (var glyph in glyphs.DistinctBy(glyph => Convert.ToHexString(glyph.Bytes)))
        {
            var key = Convert.ToHexString(glyph.Bytes);
            var code = Convert.ToInt32(key, 16);
            int gid;
            if (composite)
            {
                gid = cidBytes is null ? code : U16(cidBytes, checked(code * 2));
            }
            else if (symbolic)
            {
                gid = Lookup(code + 0xf000, true);
                if (gid == 0)
                {
                    gid = Lookup(code, true);
                }
            }
            else
            {
                var text = font.EncodingUnicode.GetValueOrDefault(key);
                var runes = text?.EnumerateRunes().ToArray();
                if (runes is not { Length: 1 })
                {
                    throw new NotSupportedException($"Cannot map font code {key} to a glyph.");
                }
                gid = Lookup(runes[0].Value, false);
            }
            if (gid <= 0 || gid >= glyphCount)
            {
                throw new NotSupportedException($"Embedded font has no mapped glyph for code {key}.");
            }
            if (!mapping.TryGetValue(gid, out var character))
            {
                if (mapping.Count >= 60000)
                {
                    throw new NotSupportedException("Embedded font glyph budget exceeded.");
                }
                character = 0xf0000 + mapping.Count;
                mapping.Add(gid, character);
            }
            Characters.Add(key, char.ConvertFromUtf32(character));
            var units = U16(tables["head"], 18);
            var format = U16(tables["head"], 50);
            if (units == 0 || format > 1)
            {
                throw new InvalidDataException("Invalid TrueType glyph units or locations.");
            }
            var first = format == 0 ? U16(tables["loca"], gid * 2) * 2 : U32(tables["loca"], gid * 4);
            var end = format == 0 ? U16(tables["loca"], (gid + 1) * 2) * 2 : U32(tables["loca"], (gid + 1) * 4);
            if (first > end || end > tables["glyf"].Length || first != end && end - first < 10)
            {
                throw new InvalidDataException("Invalid TrueType glyph extent.");
            }
            var bounds = first == end ? new PdfGlyphBounds(0, 0, 0, 0) : new PdfGlyphBounds(
                BinaryPrimitives.ReadInt16BigEndian(tables["glyf"].AsSpan(first + 2, 2)) / (double)units,
                BinaryPrimitives.ReadInt16BigEndian(tables["glyf"].AsSpan(first + 4, 2)) / (double)units,
                BinaryPrimitives.ReadInt16BigEndian(tables["glyf"].AsSpan(first + 6, 2)) / (double)units,
                BinaryPrimitives.ReadInt16BigEndian(tables["glyf"].AsSpan(first + 8, 2)) / (double)units);
            if (bounds.Left > bounds.Right || bounds.Bottom > bounds.Top)
            {
                throw new InvalidDataException("Invalid TrueType glyph bounds.");
            }
            if (!tables.ContainsKey("gvar") && !tables.ContainsKey("fvar"))
            {
                GlyphBounds.Add(key, bounds);
            }
        }
        // One format-12 Unicode cmap; each used PDF glyph gets an independent code.
        var cmap = new byte[28 + mapping.Count * 12];
        W16(cmap, 2, 1);
        W16(cmap, 4, 3);
        W16(cmap, 6, 10);
        W32(cmap, 8, 12);
        W16(cmap, 12, 12);
        W32(cmap, 16, (uint)(cmap.Length - 12));
        W32(cmap, 24, (uint)mapping.Count);
        var group = 28;
        foreach (var pair in mapping.OrderBy(pair => pair.Value))
        {
            W32(cmap, group, (uint)pair.Value);
            W32(cmap, group + 4, (uint)pair.Value);
            W32(cmap, group + 8, (uint)pair.Key);
            group += 12;
        }
        tables["cmap"] = cmap;
        // PDF already specifies glyph selection and placement; do not shape again.
        foreach (var tag in new[] { "DSIG", "GSUB", "GPOS", "GDEF", "kern" })
        {
            tables.Remove(tag);
        }
        if (redactionSubset)
        {
            PdfRedactionFontSubset.Apply(tables, mapping.Keys);
        }
        W32(tables["head"], 8, 0);
        var output = new byte[12 + tables.Count * 16 + tables.Values.Sum(data => (data.Length + 3) & ~3)];
        W32(output, 0, 0x10000);
        W16(output, 4, tables.Count);
        var power = 1;
        var selector = 0;
        while (power * 2 <= tables.Count)
        {
            power *= 2;
            selector++;
        }
        W16(output, 6, power * 16);
        W16(output, 8, selector);
        W16(output, 10, tables.Count * 16 - power * 16);
        var position = 12 + tables.Count * 16;
        var directory = 12;
        var head = 0;
        foreach (var pair in tables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Encoding.ASCII.GetBytes(pair.Key).CopyTo(output, directory);
            W32(output, directory + 4, Checksum(pair.Value));
            W32(output, directory + 8, (uint)position);
            W32(output, directory + 12, (uint)pair.Value.Length);
            pair.Value.CopyTo(output, position);
            if (pair.Key == "head")
            {
                head = position;
            }
            position += (pair.Value.Length + 3) & ~3;
            directory += 16;
        }
        W32(output, head + 8, unchecked(0xb1b0afba - Checksum(output)));
        Font = new("pdfkit" + Convert.ToHexString(SHA256.HashData(output)), font.Name, Convert.ToBase64String(output));
    }

    private int Lookup(int code, bool symbolic)
    {
        if (!tables.TryGetValue("cmap", out var cmap))
        {
            throw new NotSupportedException("Simple TrueType font requires a supported cmap.");
        }
        var candidates = new List<(int Offset, int Format)>();
        for (var i = 0; i < U16(cmap, 2); i++)
        {
            var p = 4 + i * 8;
            var platform = U16(cmap, p);
            var encoding = U16(cmap, p + 2);
            var offset = U32(cmap, p + 4);
            var format = U16(cmap, offset);
            if ((symbolic ? platform == 3 && encoding == 0 : platform == 0 || platform == 3 && encoding is 1 or 10) && format is 4 or 12)
            {
                candidates.Add((offset, format));
            }
        }
        foreach (var (offset, format) in candidates.OrderByDescending(item => item.Format))
        {
            if (format == 12)
            {
                var groups = U32(cmap, offset + 12);
                if (groups > cmap.Length / 12)
                {
                    throw new InvalidDataException("Invalid cmap groups.");
                }
                for (var i = 0; i < groups; i++)
                {
                    var p = offset + 16 + i * 12;
                    if (code >= U32(cmap, p) && code <= U32(cmap, p + 4))
                    {
                        return checked(U32(cmap, p + 8) + code - U32(cmap, p));
                    }
                }
            }
            else if (code <= 65535)
            {
                var count = U16(cmap, offset + 6) / 2;
                for (var i = 0; i < count; i++)
                {
                    var end = U16(cmap, offset + 14 + i * 2);
                    var start = U16(cmap, offset + 16 + count * 2 + i * 2);
                    if (code < start || code > end)
                    {
                        continue;
                    }
                    var delta = U16(cmap, offset + 16 + count * 4 + i * 2);
                    var p = offset + 16 + count * 6 + i * 2;
                    var range = U16(cmap, p);
                    var gid = range == 0 ? code : U16(cmap, p + range + (code - start) * 2);
                    return range != 0 && gid == 0 ? 0 : (gid + delta) & 65535;
                }
            }
        }
        return 0;
    }

    private static uint Checksum(byte[] bytes)
    {
        uint sum = 0;
        for (var i = 0; i < bytes.Length; i += 4)
        {
            uint word = 0;
            for (var j = 0; j < 4; j++)
            {
                word = (word << 8) | (i + j < bytes.Length ? bytes[i + j] : 0u);
            }
            sum = unchecked(sum + word);
        }
        return sum;
    }
}
