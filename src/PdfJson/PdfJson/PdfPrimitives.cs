using System.Globalization;
using System.Text;

namespace Practical.PdfJson;

internal static class PdfPrimitives
{
    public static PdfValue Num(double n)
    {
        if (!double.IsFinite(n))
        {
            throw new InvalidDataException("PDF numbers must be finite.");
        }

        var text = n.ToString("G17", CultureInfo.InvariantCulture);
        var e = text.IndexOf('E');
        if (e >= 0)
        {
            var negative = text[0] == '-';
            var mantissa = text[(negative ? 1 : 0)..e];
            var dot = mantissa.IndexOf('.');
            var digits = mantissa.Replace(".", "");
            var position = (dot < 0 ? mantissa.Length : dot) + int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
            text = position <= 0 ? "0." + new string ('0', -position) + digits : position >= digits.Length ? digits + new string ('0', position - digits.Length) : digits.Insert(position, ".");
            if (negative)
            {
                text = "-" + text;
            }
        }

        return new()
        {
            Type = "number",
            Number = text
        };
    }

    public static PdfValue Name(string n) => new()
    {
        Type = "name",
        Name = n
    };
    public static PdfValue Ref(PdfReference id) => new()
    {
        Type = "reference",
        Reference = id
    };
    public static PdfValue Str(byte[] bytes) => new()
    {
        Type = "string",
        Base64 = Convert.ToBase64String(bytes)
    };
    public static PdfValue Array(params PdfValue[] items) => new()
    {
        Type = "array",
        Items = items.ToList()
    };
    public static PdfValue Dict(params (string Key, PdfValue Value)[] items) => new()
    {
        Type = "dictionary",
        Entries = items.ToDictionary(x => x.Key, x => x.Value)
    };
    public static PdfValue Stream(byte[] bytes)
    {
        var value = Dict();
        value.Type = "stream";
        value.Base64 = Convert.ToBase64String(bytes);
        return value;
    }

    public static string Syntax(PdfValue value) => value.Type switch
    {
        "number" => value.Number!,
        "name" => "/" + string.Concat(Encoding.Latin1.GetBytes(value.Name!).Select(b => b >= 33 && b <= 126 && !"()<>[]{}/%#".Contains((char)b) ? ((char)b).ToString() : "#" + b.ToString("X2"))),
        "string" => "<" + Convert.ToHexString(Convert.FromBase64String(value.Base64!)) + ">",
        "array" => "[" + string.Join(" ", value.Items!.Select(Syntax)) + "]",
        "boolean" => value.Boolean == true ? "true" : "false",
        "null" => "null",
        _ => throw new InvalidDataException("Unsupported content operand.")};
    public static double Number(PdfValue value) => double.Parse(value.Number ?? throw new InvalidDataException("Expected number."), CultureInfo.InvariantCulture);
}

internal sealed class PdfGraph(PdfObjectDocument document)
{
    private readonly Dictionary<PdfReference, PdfValue> objects = document.Objects.ToDictionary(o => o.Id, o => o.Value);
    public PdfValue Resolve(PdfValue value)
    {
        var seen = new HashSet<PdfReference>();
        while (value.Reference is { } id)
        {
            if (!seen.Add(id) || !objects.TryGetValue(id, out value!))
            {
                throw new InvalidDataException("Invalid/cyclic indirect reference.");
            }
        }

        return value;
    }

    public PdfValue? Get(PdfValue value, string key) => Resolve(value).Entries?.GetValueOrDefault(key)is { } item ? Resolve(item) : null;
    public double Number(PdfValue value, string key, double fallback = 0) => Get(value, key)is { } item ? PdfPrimitives.Number(item) : fallback;
    public List<NativePage> Pages()
    {
        var result = new List<NativePage>();
        var visited = new HashSet<PdfValue>();
        void Walk(PdfValue node, PdfValue? resources, PdfValue? media, int depth)
        {
            node = Resolve(node);
            if (depth > 100 || !visited.Add(node))
            {
                throw new InvalidDataException("Invalid page tree.");
            }

            resources = Get(node, "Resources") ?? resources;
            media = Get(node, "MediaBox") ?? media;
            if (Get(node, "Type")?.Name == "Page")
            {
                var box = media?.Items?.Select(PdfPrimitives.Number).ToArray() ?? [0, 0, 612, 792];
                if (box.Length != 4)
                {
                    throw new InvalidDataException("Invalid MediaBox.");
                }

                var streams = new List<(PdfReference Id, PdfValue Value)>();
                void Contents(PdfValue item, int nesting = 0)
                {
                    if (nesting > 100)
                    {
                        throw new InvalidDataException("Cyclic content array.");
                    }

                    var value = Resolve(item);
                    if (value.Type == "array")
                    {
                        foreach (var child in value.Items!)
                        {
                            Contents(child, nesting + 1);
                        }
                    }
                    else if (value.Type == "stream" && item.Reference is { } id)
                    {
                        streams.Add((id, value));
                    }
                    else
                    {
                        throw new InvalidDataException("Expected indirect content stream.");
                    }
                }

                if (node.Entries!.TryGetValue("Contents", out var content))
                {
                    Contents(content);
                }

                result.Add(new(result.Count + 1, node, resources ?? PdfPrimitives.Dict(), box[2] - box[0], box[3] - box[1], streams));
            }
            else
            {
                foreach (var child in Get(node, "Kids")?.Items ?? throw new InvalidDataException("Page tree Kids missing."))
                {
                    Walk(child, resources, media, depth + 1);
                }
            }
        }

        Walk(Get(Resolve(document.Trailer.Entries!["Root"]), "Pages")!, null, null, 0);
        return result;
    }
}

internal sealed record NativePage(int Number, PdfValue Dictionary, PdfValue Resources, double Width, double Height, List<(PdfReference Id, PdfValue Value)> Streams);
internal sealed record PdfOperation(string Operator, List<PdfValue> Operands, PdfValue? InlineImage = null)
{
    public string Serialize() => string.Join(" ", Operands.Select(PdfPrimitives.Syntax)) + " " + Operator + "\n";
}

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
