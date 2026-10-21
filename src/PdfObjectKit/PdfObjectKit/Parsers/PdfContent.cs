using PdfObjectKit.Primitives;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using System.Globalization;
using System.Text;

namespace PdfObjectKit.Parsers;

internal static class PdfContent
{
    public static List<PdfOperation> Parse(byte[] bytes)
    {
        var reader = new PdfSyntaxReader(bytes);
        var result = new List<PdfOperation>();
        var operands = new List<PdfValue>();
        while (true)
        {
            reader.Skip();
            if (reader.Position == bytes.Length)
            {
                break;
            }

            var b = (char)bytes[reader.Position];
            if ("/([<+-.0123456789".Contains(b))
            {
                operands.Add(reader.Value());
                continue;
            }

            var op = reader.Word();
            if (op is "true" or "false" or "null")
            {
                operands.Add(op == "null" ? new() { Type = "null" } : new() { Type = "boolean", Boolean = op == "true" });
                continue;
            }

            if (op == "BI")
            {
                if (operands.Count != 0)
                {
                    throw new InvalidDataException("Unexpected inline image operands.");
                }

                var image = PdfPrimitives.Dict();
                while (!reader.Take("ID"))
                {
                    var key = reader.Value().Name ?? throw new InvalidDataException("Invalid inline image key.");
                    key = key switch
                    {
                        "W" => "Width",
                        "H" => "Height",
                        "BPC" => "BitsPerComponent",
                        "CS" => "ColorSpace",
                        "F" => "Filter",
                        "IM" => "ImageMask",
                        _ => key
                    };
                    image.Entries![key] = reader.Value();
                }

                if (reader.Position >= bytes.Length || !PdfSyntaxReader.White(bytes[reader.Position]))
                {
                    throw new InvalidDataException("Inline image ID requires whitespace.");
                }

                if (bytes[reader.Position++] == 13 && reader.Position < bytes.Length && bytes[reader.Position] == 10)
                {
                    reader.Position++;
                }

                // Deterministic boundaries for unfiltered images; never search binary data for EI.
                if (image.Entries!.ContainsKey("Filter"))
                {
                    throw new NotSupportedException("Filtered inline images are not yet indexed.");
                }

                int N(string key, int fallback) => image.Entries.TryGetValue(key, out var v) ? checked((int)PdfPrimitives.Number(v)) : fallback;
                var mask = image.Entries.GetValueOrDefault("ImageMask")?.Boolean == true;
                var color = image.Entries.GetValueOrDefault("ColorSpace")?.Name;
                var components = mask ? 1 : color switch
                {
                    "RGB" or "DeviceRGB" => 3,
                    "CMYK" or "DeviceCMYK" => 4,
                    "G" or "DeviceGray" => 1,
                    _ => throw new NotSupportedException("Unsupported inline image color space.")};
                var width = N("Width", 0);
                var height = N("Height", 0);
                var bits = N("BitsPerComponent", mask ? 1 : 8);
                if (width <= 0 || height <= 0 || bits <= 0 || bits > 16)
                {
                    throw new InvalidDataException("Invalid inline image dimensions.");
                }

                var length = checked((int)(((long)width * components * bits + 7) / 8 * height));
                if (length > bytes.Length - reader.Position)
                {
                    throw new InvalidDataException("Truncated inline image.");
                }

                image.Base64 = Convert.ToBase64String(bytes, reader.Position, length);
                image.Type = "stream";
                reader.Position += length;
                reader.Expect("EI");
                result.Add(new(op, [], image));
                continue;
            }

            result.Add(new(op, operands));
            operands = [];
        }

        if (operands.Count != 0)
        {
            throw new InvalidDataException("Content operands without operator.");
        }

        return result;
    }
}
