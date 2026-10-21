using PdfObjectKit.Primitives;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using System.Globalization;
using System.Text;

namespace PdfObjectKit.Parsers;

internal sealed record PdfOperation(string Operator, List<PdfValue> Operands, PdfValue? InlineImage = null)
{
    public string Serialize() => string.Join(" ", Operands.Select(PdfPrimitives.Syntax)) + " " + Operator + "\n";
}
