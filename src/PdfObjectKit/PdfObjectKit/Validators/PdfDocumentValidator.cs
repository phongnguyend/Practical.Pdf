using PdfObjectKit.Models;
using System.Text.RegularExpressions;

namespace PdfObjectKit.Validators;

/// <summary>Shared structural validation for reading, writing, conversion, and editing.</summary>
internal static class PdfDocumentValidator
{
    public static void Validate(PdfObjectDocument document, bool allowEncrypted = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidDataException(message);
            }
        }

        Require(document.Format == "pdf-object-graph" && document.SchemaVersion == 1, "Unsupported object graph format/version.");
        Require(document.PdfVersion is not null && Regex.IsMatch(document.PdfVersion, @"\A(?:1\.[0-7]|2\.0)\z"), "Invalid PDF version.");
        Require(document.Objects is { Count: > 0 }, "The object table must not be empty.");
        var ids = new HashSet<PdfReference>();
        var numbers = new HashSet<long>();
        foreach (var obj in document.Objects!)
        {
            Require(obj?.Id is not null && obj.Id.ObjectNumber is> 0 and < int.MaxValue && obj.Id.Generation is >= 0 and < 65535, "Invalid object identifier.");
            Require(ids.Add(obj!.Id) && numbers.Add(obj.Id.ObjectNumber), "Duplicate object number.");
        }

        void Visit(PdfValue? value, int depth, bool indirect = false)
        {
            Require(value is not null && depth <= 100, "Null node or excessively deep PDF structure.");
            var v = value!;
            var fields = (v.Number is null ? 0 : 1) + (v.Boolean is null ? 0 : 1) + (v.Name is null ? 0 : 1) + (v.Base64 is null ? 0 : 1) + (v.Reference is null ? 0 : 1) + (v.Items is null ? 0 : 1) + (v.Entries is null ? 0 : 1);
            Require(fields == (v.Type == "null" ? 0 : v.Type == "stream" ? 2 : 1), "Unexpected/missing value fields.");
            switch (v.Type)
            {
                case "null":
                    break;
                case "boolean":
                    Require(v.Boolean.HasValue, "Missing boolean.");
                    break;
                case "number":
                    Require(v.Number is not null && Regex.IsMatch(v.Number, @"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)\z"), "Invalid PDF number.");
                    break;
                case "name":
                    Require(v.Name is not null && v.Name.All(c => c is> '\0' and <= '\u00ff'), "Invalid PDF name bytes.");
                    break;
                case "string":
                    Require(v.Base64 is not null, "Missing string bytes.");
                    _ = Convert.FromBase64String(v.Base64!);
                    break;
                case "reference":
                    Require(v.Reference is not null && ids.Contains(v.Reference), "Dangling object reference.");
                    break;
                case "array":
                    Require(v.Items is not null, "Missing array items.");
                    foreach (var item in v.Items!)
                    {
                        Visit(item, depth + 1);
                    }

                    break;
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
                        Require(pair.Key.All(c => c is> '\0' and <= '\u00ff'), "Invalid dictionary name bytes.");
                        Visit(pair.Value, depth + 1);
                    }

                    break;
                default:
                    throw new InvalidDataException($"Unknown PDF value type: {v.Type}");
            }
        }

        foreach (var obj in document.Objects!)
        {
            Visit(obj.Value, 0, true);
        }

        Visit(document.Trailer, 0);
        Require(document.Trailer.Type == "dictionary" && document.Trailer.Entries!.TryGetValue("Root", out var root) && root.Type == "reference", "Trailer requires a Root reference.");
        Require(allowEncrypted || !document.Trailer.Entries!.ContainsKey("Encrypt"), "Encrypted object graphs are not supported.");
    }
}
