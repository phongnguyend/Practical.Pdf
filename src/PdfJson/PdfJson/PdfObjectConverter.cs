using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Practical.PdfJson;

/// <summary>Serializes the current PDF object graph, independently of page rendering.</summary>
public static class PdfObjectConverter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 256
    };
    private static readonly string[] TrailerStorageKeys =
        ["Size", "Prev", "XRefStm", "Type", "W", "Index", "Length", "Filter", "DecodeParms"];

    public static PdfObjectDocument ReadPdf(byte[] bytes, bool includeIndexes = true)
    {
        var result = PdfNativeParser.Parse(bytes);
        Validate(result);
        if (includeIndexes)
        {
            result.TextIndex = PdfTextIndexer.Build(result);
            result.ImageIndex = PdfImageIndexer.Build(result);
        }
        return result;
    }
    public static string ToJson(PdfObjectDocument document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static PdfObjectDocument FromJson(string json)
    {
        var document = JsonSerializer.Deserialize<PdfObjectDocument>(json, Options)
            ?? throw new InvalidDataException("Expected a PDF object document.");
        Validate(document);
        return document;
    }

    public static byte[] WritePdf(PdfObjectDocument document, PdfTextEditOptions? textOptions = null)
    {
        var original = WriteObjects(document);
        return PdfIndexedTextEditor.Apply(document, original, textOptions ?? new());
    }

    internal static byte[] WriteObjects(PdfObjectDocument document)
    {
        Validate(document);
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        void Name(string name)
        {
            Write("/");
            foreach (var c in name)
                Write(c is >= '!' and <= '~' && !"()<>[]{}/%#".Contains(c)
                    ? c.ToString() : "#" + ((byte)c).ToString("X2", CultureInfo.InvariantCulture));
        }
        void Dictionary(Dictionary<string, PdfValue> entries, int? length = null)
        {
            Write("<<");
            foreach (var pair in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (length.HasValue && pair.Key == "Length") continue;
                Write("\n"); Name(pair.Key); Write(" "); Value(pair.Value);
            }
            if (length.HasValue) Write($"\n/Length {length.Value.ToString(CultureInfo.InvariantCulture)}");
            Write("\n>>");
        }
        void Value(PdfValue value)
        {
            switch (value.Type)
            {
                case "null": Write("null"); break;
                case "boolean": Write(value.Boolean!.Value ? "true" : "false"); break;
                case "number": Write(value.Number!); break;
                case "name": Name(value.Name!); break;
                case "string": Write("<" + Convert.ToHexString(Convert.FromBase64String(value.Base64!)) + ">"); break;
                case "reference": Write(FormattableString.Invariant($"{value.Reference!.ObjectNumber} {value.Reference.Generation} R")); break;
                case "array":
                    Write("["); foreach (var item in value.Items!) { Value(item); Write(" "); } Write("]"); break;
                case "dictionary": Dictionary(value.Entries!); break;
                case "stream":
                    var data = Convert.FromBase64String(value.Base64!);
                    Dictionary(value.Entries!, data.Length);
                    Write("\nstream\n"); output.Write(data); Write("\nendstream"); break;
            }
        }

        Write($"%PDF-{document.PdfVersion}\n");
        output.Write([37, 226, 227, 207, 211, 10]);
        var offsets = new List<(PdfReference Id, long Offset)>();
        foreach (var obj in document.Objects.OrderBy(o => o.Id.ObjectNumber))
        {
            offsets.Add((obj.Id, output.Position));
            Write(FormattableString.Invariant($"{obj.Id.ObjectNumber} {obj.Id.Generation} obj\n"));
            Value(obj.Value); Write("\nendobj\n");
        }
        var xrefOffset = output.Position;
        Write("xref\n0 1\n0000000000 65535 f \n");
        // Sparse subsections avoid allocating an array up to the highest object number.
        foreach (var (id, offset) in offsets)
        {
            if (offset > 9_999_999_999) throw new InvalidDataException("PDF exceeds classic xref offset range.");
            Write(FormattableString.Invariant($"{id.ObjectNumber} 1\n{offset:D10} {id.Generation:D5} n \n"));
        }
        var trailerEntries = new Dictionary<string, PdfValue>(document.Trailer.Entries!);
        foreach (var key in TrailerStorageKeys) trailerEntries.Remove(key);
        trailerEntries["Size"] = new() { Type = "number", Number = (offsets.Max(o => o.Id.ObjectNumber) + 1).ToString(CultureInfo.InvariantCulture) };
        Write("trailer\n"); Dictionary(trailerEntries);
        Write(FormattableString.Invariant($"\nstartxref\n{xrefOffset}\n%%EOF\n"));
        return output.ToArray();
    }

    public static void Validate(PdfObjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        Require(document.Format == "pdf-object-graph" && document.SchemaVersion == 1, "Unsupported object graph format/version.");
        Require(document.PdfVersion is not null && Regex.IsMatch(document.PdfVersion, @"\A(?:1\.[0-7]|2\.0)\z"), "Invalid PDF version.");
        Require(document.Objects is { Count: > 0 }, "The object table must not be empty.");
        var ids = new HashSet<PdfReference>();
        var numbers = new HashSet<long>();
        foreach (var obj in document.Objects!)
        {
            Require(obj?.Id is not null && obj.Id.ObjectNumber is > 0 and < int.MaxValue && obj.Id.Generation is >= 0 and < 65535, "Invalid object identifier.");
            Require(ids.Add(obj!.Id) && numbers.Add(obj.Id.ObjectNumber), "Duplicate object number.");
        }
        void Visit(PdfValue? value, int depth, bool indirect = false)
        {
            Require(value is not null && depth <= 100, "Null node or excessively deep PDF structure.");
            var v = value!;
            var fields = (v.Number is null ? 0 : 1) + (v.Boolean is null ? 0 : 1) + (v.Name is null ? 0 : 1)
                + (v.Base64 is null ? 0 : 1) + (v.Reference is null ? 0 : 1) + (v.Items is null ? 0 : 1) + (v.Entries is null ? 0 : 1);
            Require(fields == (v.Type == "null" ? 0 : v.Type == "stream" ? 2 : 1), "Unexpected/missing value fields.");
            switch (v.Type)
            {
                case "null": break;
                case "boolean": Require(v.Boolean.HasValue, "Missing boolean."); break;
                case "number": Require(v.Number is not null && Regex.IsMatch(v.Number, @"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)\z"), "Invalid PDF number."); break;
                case "name": Require(v.Name is not null && v.Name.All(c => c is > '\0' and <= '\u00ff'), "Invalid PDF name bytes."); break;
                case "string": Require(v.Base64 is not null, "Missing string bytes."); _ = Convert.FromBase64String(v.Base64!); break;
                case "reference": Require(v.Reference is not null && ids.Contains(v.Reference), "Dangling object reference."); break;
                case "array": Require(v.Items is not null, "Missing array items."); foreach (var item in v.Items!) Visit(item, depth + 1); break;
                case "stream":
                case "dictionary":
                    Require(v.Entries is not null, "Missing dictionary entries.");
                    if (v.Type == "stream")
                    {
                        Require(indirect && v.Base64 is not null, "Streams must be indirect objects with Base64 data.");
                        _ = Convert.FromBase64String(v.Base64!);
                    }
                    foreach (var pair in v.Entries!)
                    {
                        Require(pair.Key.All(c => c is > '\0' and <= '\u00ff'), "Invalid dictionary name bytes.");
                        Visit(pair.Value, depth + 1);
                    }
                    break;
                default: throw new InvalidDataException($"Unknown PDF value type: {v.Type}");
            }
        }
        foreach (var obj in document.Objects!) Visit(obj.Value, 0, true);
        Visit(document.Trailer, 0);
        Require(document.Trailer.Type == "dictionary" && document.Trailer.Entries!.TryGetValue("Root", out var root) && root.Type == "reference", "Trailer requires a Root reference.");
        Require(!document.Trailer.Entries!.ContainsKey("Encrypt"), "Encrypted object graphs are not supported.");
    }
}
