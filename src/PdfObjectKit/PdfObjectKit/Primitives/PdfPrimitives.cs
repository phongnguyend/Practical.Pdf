using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using System.Globalization;
using System.Text;

namespace PdfObjectKit.Primitives;

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
        "dictionary" => "<< " + string.Join(" ", value.Entries!.Select(entry => Syntax(Name(entry.Key)) + " " + Syntax(entry.Value))) + " >>",
        "boolean" => value.Boolean == true ? "true" : "false",
        "null" => "null",
        _ => throw new InvalidDataException("Unsupported content operand.")};
    public static double Number(PdfValue value) => double.Parse(value.Number ?? throw new InvalidDataException("Expected number."), CultureInfo.InvariantCulture);
}
