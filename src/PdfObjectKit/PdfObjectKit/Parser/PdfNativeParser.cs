using System.Globalization;
using System.Text;

namespace PdfObjectKit;

/// <summary>Dependency-free, strict PDF object parser. Does not interpret page content or fonts.</summary>
public sealed class PdfNativeParser
{
    private readonly byte[] bytes;
    private readonly Dictionary<long, Location> locations = [];
    private readonly Dictionary<PdfReference, PdfValue> loaded = [];
    private readonly HashSet<PdfReference> loading = [];
    private readonly HashSet<int> xrefs = [];
    private readonly Dictionary<string, PdfValue> trailer = [];
    private readonly Dictionary<long, Dictionary<long, PdfValue>> objectStreams = [];
    private sealed record Location(int Type, long Offset, int GenerationOrIndex);
    private PdfNativeParser(byte[] bytes) => this.bytes = bytes;
    public static PdfObjectDocument Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new PdfNativeParser(bytes).ParseDocument();
    }

    private PdfObjectDocument ParseDocument()
    {
        if (bytes.Length < 8 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            throw new InvalidDataException("Expected PDF header at byte 0.");
        }

        var version = Encoding.ASCII.GetString(bytes, 5, 3);
        if (version is not ("1.0" or "1.1" or "1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7" or "2.0"))
        {
            throw new NotSupportedException($"Unsupported PDF version {version}.");
        }

        var tail = Encoding.Latin1.GetString(bytes);
        var start = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidDataException("Missing startxref.");
        }

        ReadXref(Offset(new PdfSyntaxReader(bytes, start + 9).Integer()));
        if (trailer.TryGetValue("Encrypt", out var encryption) && encryption.Type != "null")
        {
            throw new NotSupportedException("Encrypted PDFs must be decrypted before parsing.");
        }

        if (!trailer.TryGetValue("Root", out var root) || root.Reference is null)
        {
            throw new InvalidDataException("Missing trailer Root reference.");
        }

        foreach (var key in new[]
        {
            "Size",
            "Prev",
            "XRefStm",
            "Type",
            "W",
            "Index",
            "Length",
            "Filter",
            "DecodeParms",
            "Encrypt"
        }

        )
        {
            trailer.Remove(key);
        }

        var document = new PdfObjectDocument
        {
            PdfVersion = version,
            Trailer = new()
            {
                Type = "dictionary",
                Entries = trailer
            }
        };
        foreach (var(number, location)in locations.OrderBy(p => p.Key))
        {
            if (number == 0 || location.Type == 0)
            {
                continue;
            }

            var id = new PdfReference(number, location.Type == 2 ? 0 : location.GenerationOrIndex);
            var value = Load(id);
            var entries = value.Entries;
            if (entries?.ContainsKey("Linearized") == true || entries?.GetValueOrDefault("Type")?.Name is "ObjStm" or "XRef")
            {
                continue;
            }

            document.Objects.Add(new(id, value));
        }

        return document;
    }

    private void ReadXref(int offset, bool followPrevious = true)
    {
        if (!xrefs.Add(offset))
        {
            throw new InvalidDataException("Cyclic or duplicate cross-reference chain.");
        }

        if (xrefs.Count > 1024)
        {
            throw new InvalidDataException("Too many cross-reference sections.");
        }

        var reader = new PdfSyntaxReader(bytes, offset);
        var current = new Dictionary<long, Location>();
        PdfValue dictionary;
        if (reader.Take("xref"))
        {
            while (!reader.Take("trailer"))
            {
                var first = reader.Integer();
                var count = Count(reader.Integer());
                if (first < 0 || first + count > int.MaxValue)
                {
                    throw reader.Error("Invalid xref subsection");
                }

                for (var i = 0; i < count; i++)
                {
                    var position = reader.Integer();
                    var generation = Count(reader.Integer(), 65535);
                    var flag = reader.Word();
                    if (flag is not ("n" or "f"))
                    {
                        throw reader.Error("Invalid xref entry");
                    }

                    if (!current.TryAdd(first + i, new(flag == "n" ? 1 : 0, position, generation)))
                    {
                        throw reader.Error("Duplicate xref entry");
                    }
                }
            }

            dictionary = reader.Value();
        }
        else
        {
            dictionary = ReadIndirect(offset).Value;
            if (dictionary.Type != "stream" || dictionary.Entries?.GetValueOrDefault("Type")?.Name != "XRef")
            {
                throw reader.Error("Expected cross-reference stream");
            }

            var data = PdfStreamDecoder.Decode(dictionary);
            var widths = ArrayNumbers(dictionary.Entries["W"]);
            if (widths.Length != 3 || widths.Any(w => w < 0 || w > 8) || widths.Sum() == 0)
            {
                throw reader.Error("Invalid xref field widths");
            }

            var ranges = dictionary.Entries.TryGetValue("Index", out var index) ? ArrayNumbers(index) : [0L, Number(dictionary.Entries["Size"])];
            if (ranges.Length % 2 != 0)
            {
                throw reader.Error("Invalid xref Index");
            }

            var position = 0;
            long Field(int width)
            {
                ulong value = 0;
                for (var i = 0; i < width; i++)
                {
                    if (position >= data.Length)
                    {
                        throw reader.Error("Truncated xref stream");
                    }

                    value = (value << 8) | data[position++];
                }

                if (value > long.MaxValue)
                {
                    throw reader.Error("Xref value exceeds supported range");
                }

                return (long)value;
            }

            for (var range = 0; range < ranges.Length; range += 2)
            {
                var first = ranges[range];
                var count = Count(ranges[range + 1]);
                if (first < 0 || first + count > int.MaxValue)
                {
                    throw reader.Error("Invalid xref range");
                }

                for (var i = 0; i < count; i++)
                {
                    var type = widths[0] == 0 ? 1 : Field((int)widths[0]);
                    var field = Field((int)widths[1]);
                    var gen = Count(Field((int)widths[2]));
                    if (type is < 0 or > 2)
                    {
                        throw new NotSupportedException($"Unsupported cross-reference type {type}.");
                    }

                    if (!current.TryAdd(first + i, new((int)type, field, gen)))
                    {
                        throw reader.Error("Duplicate xref entry");
                    }
                }
            }

            if (position != data.Length)
            {
                throw reader.Error("Unexpected extra xref stream data");
            }
        }

        if (dictionary.Entries is null)
        {
            throw reader.Error("Expected trailer dictionary");
        }

        foreach (var pair in dictionary.Entries)
        {
            trailer.TryAdd(pair.Key, pair.Value);
        }

        // Hybrid xref streams supersede table placeholders in the same revision.
        if (dictionary.Entries.TryGetValue("XRefStm", out var hybrid))
        {
            ReadXref(Offset(Number(hybrid)), false);
        }

        foreach (var pair in current)
        {
            locations.TryAdd(pair.Key, pair.Value);
            if (locations.Count > 1_000_000)
            {
                throw reader.Error("Too many objects");
            }
        }

        if (followPrevious && dictionary.Entries.TryGetValue("Prev", out var previous))
        {
            ReadXref(Offset(Number(previous)));
        }
    }

    private PdfValue Load(PdfReference id)
    {
        if (loaded.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (!locations.TryGetValue(id.ObjectNumber, out var location) || location.Type == 0 || id.Generation != (location.Type == 2 ? 0 : location.GenerationOrIndex))
        {
            throw new InvalidDataException($"Missing object {id.ObjectNumber} {id.Generation}.");
        }

        if (loading.Count > 100 || !loading.Add(id))
        {
            throw new InvalidDataException("Cyclic/excessively deep stream length or object-stream dependency.");
        }

        try
        {
            PdfValue value;
            if (location.Type == 1)
            {
                var parsed = ReadIndirect(Offset(location.Offset));
                if (parsed.Id != id)
                {
                    throw new InvalidDataException("Object header disagrees with cross-reference entry.");
                }

                value = parsed.Value;
            }
            else
            {
                if (!objectStreams.TryGetValue(location.Offset, out var contents))
                {
                    var container = Load(new(location.Offset, 0));
                    if (container.Type != "stream" || container.Entries?.GetValueOrDefault("Type")?.Name != "ObjStm")
                    {
                        throw new InvalidDataException("Expected object stream.");
                    }

                    var data = PdfStreamDecoder.Decode(container);
                    var header = new PdfSyntaxReader(data);
                    var n = Count(Number(Resolve(container.Entries["N"])));
                    var first = Count(Number(Resolve(container.Entries["First"])), data.Length);
                    var pairs = new List<(long Number, int Offset)>();
                    for (var i = 0; i < n; i++)
                    {
                        pairs.Add((header.Integer(), Count(header.Integer(), data.Length)));
                    }

                    if (header.Position > first)
                    {
                        throw header.Error("Object stream header overlaps body");
                    }

                    contents = [];
                    for (var i = 0; i < pairs.Count; i++)
                    {
                        var pair = pairs[i];
                        if (pair.Number <= 0 || pair.Offset > data.Length - first)
                        {
                            throw header.Error("Invalid compressed object location");
                        }

                        var start = first + pair.Offset;
                        var end = i + 1 < pairs.Count ? first + pairs[i + 1].Offset : data.Length;
                        if (end < start || end > data.Length)
                        {
                            throw header.Error("Invalid compressed object offsets");
                        }

                        var body = new PdfSyntaxReader(data[start..end]);
                        if (!contents.TryAdd(pair.Number, body.Value()))
                        {
                            throw body.Error("Duplicate compressed object");
                        }

                        body.Skip();
                        if (body.Position != body.Bytes.Length)
                        {
                            throw body.Error("Unexpected compressed object data");
                        }

                        if (locations.TryGetValue(pair.Number, out var source) && source.Type == 2 && source.Offset == location.Offset && source.GenerationOrIndex != i)
                        {
                            throw body.Error("Object stream index disagrees with xref");
                        }
                    }

                    objectStreams.Add(location.Offset, contents);
                }

                if (!contents.TryGetValue(id.ObjectNumber, out value!))
                {
                    throw new InvalidDataException("Compressed object not found.");
                }
            }

            loaded[id] = value;
            return value;
        }
        finally
        {
            loading.Remove(id);
        }
    }

    private PdfValue Resolve(PdfValue value) => value.Reference is null ? value : Load(value.Reference);
    private PdfIndirectObject ReadIndirect(int offset)
    {
        var reader = new PdfSyntaxReader(bytes, offset);
        var id = new PdfReference(reader.Integer(), Count(reader.Integer(), 65534));
        if (id.ObjectNumber <= 0)
        {
            throw reader.Error("Invalid object number");
        }

        reader.Expect("obj");
        var value = reader.Value();
        if (value.Type == "dictionary" && reader.Take("stream"))
        {
            // Only the required EOL is consumed: initial stream whitespace is data.
            if (reader.Position < bytes.Length && bytes[reader.Position] == 13)
            {
                reader.Position++;
            }
            else if (reader.Position >= bytes.Length || bytes[reader.Position] != 10)
            {
                throw reader.Error("stream must be followed by EOL");
            }

            if (reader.Position < bytes.Length && bytes[reader.Position] == 10)
            {
                reader.Position++;
            }

            if (!value.Entries!.TryGetValue("Length", out var length))
            {
                throw reader.Error("Missing stream Length");
            }

            var count = Count(Number(Resolve(length)), bytes.Length);
            if (count > bytes.Length - reader.Position)
            {
                throw reader.Error("Truncated stream");
            }

            value.Type = "stream";
            value.Base64 = Convert.ToBase64String(bytes.AsSpan(reader.Position, count));
            value.Entries.Remove("Length");
            reader.Position += count;
            reader.Expect("endstream");
        }

        reader.Expect("endobj");
        return new(id, value);
    }

    private int Offset(long value) => value >= 0 && value < bytes.Length ? (int)value : throw new InvalidDataException("PDF offset outside file.");
    internal static long Number(PdfValue value) => value.Type == "number" && long.TryParse(value.Number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : throw new InvalidDataException("Expected integer value.");
    private static long[] ArrayNumbers(PdfValue value) => value.Items?.Select(Number).ToArray() ?? throw new InvalidDataException("Expected numeric array.");
    internal static int Count(long value, int maximum = 1_000_000) => value >= 0 && value <= maximum ? (int)value : throw new InvalidDataException("PDF count exceeds supported range.");
}
