using System.Globalization;
using System.Text;

namespace PdfObjectKit;

/// <summary>Writes a PDF object graph, applying requested indexed text edits.</summary>
public static class PdfWriter
{
    private static readonly string[] TrailerStorageKeys = ["Size", "Prev", "XRefStm", "Type", "W", "Index", "Length", "Filter", "DecodeParms"];

    public static byte[] Write(PdfObjectDocument document, PdfTextEditOptions? textOptions = null)
    {
        var original = WriteObjects(document);
        return PdfIndexedTextEditor.Apply(document, original, textOptions ?? new());
    }

    internal static byte[] WriteObjects(PdfObjectDocument document)
    {
        PdfDocumentValidator.Validate(document);
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        void Name(string name)
        {
            Write("/");
            foreach (var c in name)
            {
                Write(c is >= '!' and <= '~' && !"()<>[]{}/%#".Contains(c) ? c.ToString() : "#" + ((byte)c).ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        void Dictionary(Dictionary<string, PdfValue> entries, int? length = null)
        {
            Write("<<");
            foreach (var pair in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (length.HasValue && pair.Key == "Length")
                {
                    continue;
                }

                Write("\n");
                Name(pair.Key);
                Write(" ");
                Value(pair.Value);
            }

            if (length.HasValue)
            {
                Write($"\n/Length {length.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            Write("\n>>");
        }

        void Value(PdfValue value)
        {
            switch (value.Type)
            {
                case "null":
                    Write("null");
                    break;
                case "boolean":
                    Write(value.Boolean!.Value ? "true" : "false");
                    break;
                case "number":
                    Write(value.Number!);
                    break;
                case "name":
                    Name(value.Name!);
                    break;
                case "string":
                    Write("<" + Convert.ToHexString(Convert.FromBase64String(value.Base64!)) + ">");
                    break;
                case "reference":
                    Write(FormattableString.Invariant($"{value.Reference!.ObjectNumber} {value.Reference.Generation} R"));
                    break;
                case "array":
                    Write("[");
                    foreach (var item in value.Items!)
                    {
                        Value(item);
                        Write(" ");
                    }

                    Write("]");
                    break;
                case "dictionary":
                    Dictionary(value.Entries!);
                    break;
                case "stream":
                    var data = Convert.FromBase64String(value.Base64!);
                    Dictionary(value.Entries!, data.Length);
                    Write("\nstream\n");
                    output.Write(data);
                    Write("\nendstream");
                    break;
            }
        }

        Write($"%PDF-{document.PdfVersion}\n");
        output.Write([37, 226, 227, 207, 211, 10]);
        var offsets = new List<(PdfReference Id, long Offset)>();
        foreach (var obj in document.Objects.OrderBy(o => o.Id.ObjectNumber))
        {
            offsets.Add((obj.Id, output.Position));
            Write(FormattableString.Invariant($"{obj.Id.ObjectNumber} {obj.Id.Generation} obj\n"));
            Value(obj.Value);
            Write("\nendobj\n");
        }

        var xrefOffset = output.Position;
        Write("xref\n0 1\n0000000000 65535 f \n");
        // Sparse subsections avoid allocating an array up to the highest object number.
        foreach (var(id, offset)in offsets)
        {
            if (offset > 9_999_999_999)
            {
                throw new InvalidDataException("PDF exceeds classic xref offset range.");
            }

            Write(FormattableString.Invariant($"{id.ObjectNumber} 1\n{offset:D10} {id.Generation:D5} n \n"));
        }

        var trailerEntries = new Dictionary<string, PdfValue>(document.Trailer.Entries!);
        foreach (var key in TrailerStorageKeys)
        {
            trailerEntries.Remove(key);
        }

        trailerEntries["Size"] = new()
        {
            Type = "number",
            Number = (offsets.Max(o => o.Id.ObjectNumber) + 1).ToString(CultureInfo.InvariantCulture)
        };
        Write("trailer\n");
        Dictionary(trailerEntries);
        Write(FormattableString.Invariant($"\nstartxref\n{xrefOffset}\n%%EOF\n"));
        return output.ToArray();
    }
}
