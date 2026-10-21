using System.Buffers.Binary;
using System.Text;
using static PdfObjectKit.PdfPrimitives;

namespace PdfObjectKit;

// Full TrueType embedding with an explicit CID-to-glyph map and ToUnicode CMap.
internal sealed class PdfTrueTypeFont
{
    private readonly byte[] bytes;
    private readonly Dictionary<string, (int Offset, int Length)> tables = [];
    private readonly int units, metrics, hmtx, cmap, glyphCount;
    private readonly string postScriptName;
    private int U16(int p) => BinaryPrimitives.ReadUInt16BigEndian(Slice(p, 2));
    private short I16(int p) => BinaryPrimitives.ReadInt16BigEndian(Slice(p, 2));
    private int U32(int p) => checked((int)BinaryPrimitives.ReadUInt32BigEndian(Slice(p, 4)));
    private ReadOnlySpan<byte> Slice(int p, int n) => p >= 0 && n >= 0 && p <= bytes.Length - n ? bytes.AsSpan(p, n) : throw new InvalidDataException("Truncated TrueType font.");
    private int Table(string name, int minimum)
    {
        if (!tables.TryGetValue(name, out var table) || table.Length < minimum)
        {
            throw new InvalidDataException($"Missing/truncated TrueType table {name}.");
        }

        return table.Offset;
    }

    public PdfTrueTypeFont(byte[] bytes)
    {
        this.bytes = bytes;
        if (U32(0) != 0x10000)
        {
            throw new InvalidDataException("Expected a TrueType font with glyf outlines (not CFF, WOFF or a collection).");
        }

        for (var i = 0; i < U16(4); i++)
        {
            var p = 12 + i * 16;
            var tag = Encoding.ASCII.GetString(Slice(p, 4));
            var offset = U32(p + 8);
            var length = U32(p + 12);
            Slice(offset, length);
            if (!tables.TryAdd(tag, (offset, length)))
            {
                throw new InvalidDataException("Duplicate TrueType table.");
            }
        }

        Table("glyf", 0);
        Table("loca", 0);
        units = U16(Table("head", 54) + 18);
        metrics = U16(Table("hhea", 36) + 34);
        glyphCount = U16(Table("maxp", 6) + 4);
        if (units == 0 || metrics == 0 || metrics > glyphCount)
        {
            throw new InvalidDataException("Invalid TrueType metrics.");
        }

        hmtx = Table("hmtx", metrics * 4 + (glyphCount - metrics) * 2);
        var names = Table("name", 6);
        string? selectedName = null;
        for (var i = 0; i < U16(names + 2); i++)
        {
            var p = names + 6 + i * 12;
            if (U16(p + 6) != 6 || U16(p)is not (0 or 1 or 3))
            {
                continue;
            }

            var data = Slice(names + U16(names + 4) + U16(p + 10), U16(p + 8));
            var name = U16(p) == 1 ? Encoding.Latin1.GetString(data) : Encoding.BigEndianUnicode.GetString(data);
            if (name.Length == 0 || name.Any(c => c < 33 || c > 126 || "[](){}<>/%".Contains(c)))
            {
                continue;
            }

            selectedName = name;
            if (U16(p) == 3)
            {
                break;
            }
        }

        postScriptName = selectedName ?? throw new InvalidDataException("TrueType PostScript name missing.");
        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 10 && (U16(os2.Offset + 8) & 0x202) != 0)
        {
            throw new InvalidDataException("Font embedding permissions disallow outline embedding.");
        }

        var root = Table("cmap", 4);
        var candidates = new List<(int Score, int Offset)>();
        for (var i = 0; i < U16(root + 2); i++)
        {
            var p = root + 4 + i * 8;
            var platform = U16(p);
            var encoding = U16(p + 2);
            var sub = checked(root + U32(p + 4));
            var format = U16(sub);
            if ((platform == 0 || platform == 3 && encoding is 1 or 10) && format is 4 or 12)
            {
                candidates.Add((format == 12 ? 2 : 1, sub));
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidDataException("Font has no supported Unicode cmap (format 4/12).");
        }

        cmap = candidates.OrderByDescending(c => c.Score).First().Offset;
    }

    private int Glyph(int code)
    {
        if (U16(cmap) == 12)
        {
            var count = U32(cmap + 12);
            if (count > 1_000_000)
            {
                throw new InvalidDataException("Too many cmap groups.");
            }

            for (var i = 0; i < count; i++)
            {
                var p = cmap + 16 + i * 12;
                if (code >= U32(p) && code <= U32(p + 4))
                {
                    return checked(U32(p + 8) + code - U32(p));
                }
            }
        }
        else if (code <= 65535)
        {
            var count = U16(cmap + 6) / 2;
            for (var i = 0; i < count; i++)
            {
                var end = U16(cmap + 14 + i * 2);
                var start = U16(cmap + 16 + count * 2 + i * 2);
                if (code < start || code > end)
                {
                    continue;
                }

                var delta = I16(cmap + 16 + count * 4 + i * 2);
                var p = cmap + 16 + count * 6 + i * 2;
                var range = U16(p);
                if (range == 0)
                {
                    return (code + delta) & 65535;
                }

                var glyph = U16(p + range + (code - start) * 2);
                return glyph == 0 ? 0 : (glyph + delta) & 65535;
            }
        }

        return 0;
    }

    internal sealed record Embedded(PdfValue Font, Dictionary<int, (int Cid, double Width)> Characters)
    {
        public (byte[] Bytes, double Width) Encode(string text)
        {
            using var output = new MemoryStream();
            var width = 0d;
            foreach (var rune in text.EnumerateRunes())
            {
                var item = Characters[rune.Value];
                output.WriteByte((byte)(item.Cid >> 8));
                output.WriteByte((byte)item.Cid);
                width += item.Width;
            }

            return (output.ToArray(), width);
        }
    }

    public Embedded Embed(PdfObjectDocument document, string text)
    {
        var runes = text.EnumerateRunes().Distinct().ToList();
        if (runes.Count > 65534)
        {
            throw new InvalidDataException("Too many distinct glyphs.");
        }

        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(document.Objects.Count == 0 ? 1 : document.Objects.Max(o => o.Id.ObjectNumber) + 1);
            document.Objects.Add(new(id, value));
            return Ref(id);
        }

        var characters = new Dictionary<int, (int Cid, double Width)>();
        var widths = new List<PdfValue>();
        var map = new byte[(runes.Count + 1) * 2];
        for (var i = 0; i < runes.Count; i++)
        {
            var gid = Glyph(runes[i].Value);
            if (gid <= 0 || gid >= glyphCount)
            {
                throw new InvalidDataException($"Font has no glyph for U+{runes[i].Value:X}.");
            }

            var width = U16(hmtx + Math.Min(gid, metrics - 1) * 4) / (double)units;
            characters[runes[i].Value] = (i + 1, width);
            widths.Add(Num(width * 1000));
            map[(i + 1) * 2] = (byte)(gid >> 8);
            map[(i + 1) * 2 + 1] = (byte)gid;
        }

        var head = Table("head", 54);
        var hhea = Table("hhea", 36);
        var file = Stream(bytes);
        file.Entries!["Length1"] = Num(bytes.Length);
        var descriptor = Dict(("Type", Name("FontDescriptor")), ("FontName", Name(postScriptName)), ("Flags", Num(32)), ("FontBBox", Array(Num(I16(head + 36) * 1000d / units), Num(I16(head + 38) * 1000d / units), Num(I16(head + 40) * 1000d / units), Num(I16(head + 42) * 1000d / units))), ("ItalicAngle", Num(0)), ("Ascent", Num(I16(hhea + 4) * 1000d / units)), ("Descent", Num(I16(hhea + 6) * 1000d / units)), ("CapHeight", Num(I16(hhea + 4) * 1000d / units)), ("StemV", Num(80)), ("FontFile2", Add(file)));
        var cid = Dict(("Type", Name("Font")), ("Subtype", Name("CIDFontType2")), ("BaseFont", Name(postScriptName)), ("CIDSystemInfo", Dict(("Registry", Str("Adobe"u8.ToArray())), ("Ordering", Str("Identity"u8.ToArray())), ("Supplement", Num(0)))), ("FontDescriptor", Add(descriptor)), ("CIDToGIDMap", Add(Stream(map))), ("DW", Num(1000)), ("W", Array(Num(1), new() { Type = "array", Items = widths })));
        var unicode = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /PdfObjectKitUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var chunk in characters.Chunk(100))
        {
            unicode.Append($"{chunk.Length} beginbfchar\n");
            foreach (var(code, item)in chunk)
            {
                unicode.Append($"<{item.Cid:X4}> <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(char.ConvertFromUtf32(code)))}>\n");
            }

            unicode.Append("endbfchar\n");
        }

        unicode.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return new(Add(Dict(("Type", Name("Font")), ("Subtype", Name("Type0")), ("BaseFont", Name(postScriptName)), ("Encoding", Name("Identity-H")), ("DescendantFonts", Array(Add(cid))), ("ToUnicode", Add(Stream(Encoding.ASCII.GetBytes(unicode.ToString())))))), characters);
    }
}
