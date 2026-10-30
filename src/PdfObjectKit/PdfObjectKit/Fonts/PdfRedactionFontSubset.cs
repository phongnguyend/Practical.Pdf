using System.Buffers.Binary;
using System.Text;

namespace PdfObjectKit.Fonts;

// Used only for redaction: discard unused outlines, font metadata, hint programs
// and unknown tables. Keep original glyph IDs for composite references.
internal static class PdfRedactionFontSubset
{
    private static int U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

    private static int U32(byte[] bytes, int offset) => checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));

    private static void W16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), checked((ushort)value));

    private static void W32(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), checked((uint)value));

    public static void Apply(Dictionary<string, byte[]> tables, IEnumerable<int> used)
    {
        if (tables.ContainsKey("gvar") || tables.ContainsKey("fvar"))
        {
            throw new NotSupportedException("Variable fonts cannot be redaction-subsetted.");
        }
        var head = tables["head"].AsSpan(0, 54).ToArray();
        var count = U16(tables["maxp"], 4);
        var format = U16(head, 50);
        if (format > 1 || count == 0)
        {
            throw new InvalidDataException("Invalid glyph location format.");
        }
        var offsets = Enumerable.Range(0, count + 1).Select(index => format == 0
            ? U16(tables["loca"], index * 2) * 2 : U32(tables["loca"], index * 4)).ToArray();
        for (var index = 0; index < count; index++)
        {
            if (offsets[index] > offsets[index + 1] || offsets[index + 1] > tables["glyf"].Length)
            {
                throw new InvalidDataException("Invalid glyph locations.");
            }
        }
        var kept = new Dictionary<int, byte[]>();
        var visiting = new HashSet<int>();
        void Keep(int gid, int depth)
        {
            if (gid < 0 || gid >= count || depth > 32 || visiting.Contains(gid))
            {
                throw new InvalidDataException("Invalid composite glyph dependency.");
            }
            if (kept.ContainsKey(gid))
            {
                return;
            }
            visiting.Add(gid);
            var bytes = tables["glyf"].AsSpan(offsets[gid], offsets[gid + 1] - offsets[gid]).ToArray();
            if (bytes.Length > 0)
            {
                var contours = BinaryPrimitives.ReadInt16BigEndian(bytes);
                if (contours == 0)
                {
                    bytes = bytes.AsSpan(0, 10).ToArray();
                }
                else if (contours > 0)
                {
                    var instructionOffset = 10 + contours * 2;
                    var points = U16(bytes, instructionOffset - 2) + 1;
                    var start = instructionOffset + 2 + U16(bytes, instructionOffset);
                    var end = start;
                    var coordinates = 0;
                    var processed = 0;
                    while (processed < points)
                    {
                        var flags = bytes[end++];
                        var repeat = (flags & 8) != 0 ? bytes[end++] + 1 : 1;
                        processed += repeat;
                        coordinates += repeat * (((flags & 2) != 0 ? 1 : (flags & 16) != 0 ? 0 : 2)
                            + ((flags & 4) != 0 ? 1 : (flags & 32) != 0 ? 0 : 2));
                    }
                    if (processed != points)
                    {
                        throw new InvalidDataException("Invalid glyph flag repeats.");
                    }
                    var clean = new byte[instructionOffset + 2 + end + coordinates - start];
                    bytes.AsSpan(0, instructionOffset).CopyTo(clean);
                    bytes.AsSpan(start, end + coordinates - start).CopyTo(clean.AsSpan(instructionOffset + 2));
                    bytes = clean;
                }
                else
                {
                    var end = 10;
                    int flags;
                    do
                    {
                        flags = U16(bytes, end);
                        Keep(U16(bytes, end + 2), depth + 1);
                        W16(bytes, end, flags & ~256);
                        end += 4 + ((flags & 1) != 0 ? 4 : 2)
                            + ((flags & 8) != 0 ? 2 : (flags & 64) != 0 ? 4 : (flags & 128) != 0 ? 8 : 0);
                    }
                    while ((flags & 32) != 0);
                    bytes = bytes.AsSpan(0, end).ToArray();
                }
            }
            kept.Add(gid, bytes);
            visiting.Remove(gid);
        }
        foreach (var gid in used)
        {
            Keep(gid, 0);
        }
        var loca = new byte[(count + 1) * 4];
        using var glyf = new MemoryStream();
        for (var gid = 0; gid < count; gid++)
        {
            W32(loca, gid * 4, checked((int)glyf.Length));
            if (kept.TryGetValue(gid, out var bytes))
            {
                glyf.Write(bytes);
                while (glyf.Length % 4 != 0)
                {
                    glyf.WriteByte(0);
                }
            }
        }
        W32(loca, count * 4, checked((int)glyf.Length));
        var metrics = U16(tables["hhea"], 34);
        if (metrics == 0 || metrics > count)
        {
            throw new InvalidDataException("Invalid font metrics.");
        }
        var hmtx = new byte[count * 4];
        foreach (var gid in kept.Keys)
        {
            W16(hmtx, gid * 4, U16(tables["hmtx"], Math.Min(gid, metrics - 1) * 4));
            W16(hmtx, gid * 4 + 2, U16(tables["hmtx"], gid < metrics ? gid * 4 + 2 : metrics * 4 + (gid - metrics) * 2));
        }
        tables["hhea"] = tables["hhea"].AsSpan(0, 36).ToArray();
        W16(tables["hhea"], 34, count);
        head.AsSpan(20, 16).Clear();
        W16(head, 50, 1);
        tables["head"] = head;
        tables["glyf"] = glyf.ToArray();
        tables["loca"] = loca;
        tables["hmtx"] = hmtx;
        tables["maxp"] = tables["maxp"].AsSpan(0, 32).ToArray();
        // No hint interpreter storage or instructions remain.
        tables["maxp"].AsSpan(14, 14).Clear();
        W16(tables["maxp"], 14, 1);
        var name = Encoding.BigEndianUnicode.GetBytes("PdfRedactedFont");
        var names = new byte[54 + name.Length];
        W16(names, 2, 4);
        W16(names, 4, 54);
        var ids = new[] { 1, 2, 4, 6 };
        for (var index = 0; index < ids.Length; index++)
        {
            var record = 6 + index * 12;
            W16(names, record, 3);
            W16(names, record + 2, 1);
            W16(names, record + 4, 0x409);
            W16(names, record + 6, ids[index]);
            W16(names, record + 8, name.Length);
        }
        name.CopyTo(names, 54);
        tables["name"] = names;
        tables["post"] = new byte[32];
        W32(tables["post"], 0, 0x30000);
        var os2 = new byte[96];
        W16(os2, 0, 4);
        W16(os2, 4, 400);
        W16(os2, 6, 5);
        Encoding.ASCII.GetBytes("PDFK").CopyTo(os2, 58);
        W16(os2, 62, 64);
        W16(os2, 66, 65535);
        W16(os2, 68, U16(tables["hhea"], 4));
        W16(os2, 70, U16(tables["hhea"], 6));
        W16(os2, 74, Math.Max(0, (int)BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(42))));
        W16(os2, 76, Math.Max(0, -BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(38))));
        W16(os2, 88, U16(tables["hhea"], 4));
        tables["OS/2"] = os2;
        foreach (var tag in tables.Keys.ToArray())
        {
            if (tag is not ("head" or "hhea" or "hmtx" or "maxp" or "glyf" or "loca" or "name" or "post" or "cmap" or "OS/2"))
            {
                tables.Remove(tag);
            }
        }
    }
}
