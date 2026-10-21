using PdfObjectKit.Primitives;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using System.Security.Cryptography;

namespace PdfObjectKit.Interpreters;

internal readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static PdfMatrix Identity => new(1, 0, 0, 1, 0, 0);

    public PdfImagePoint Point(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);
    public PdfMatrix Multiply(PdfMatrix rhs) => new(A * rhs.A + C * rhs.B, B * rhs.A + D * rhs.B, A * rhs.C + C * rhs.D, B * rhs.C + D * rhs.D, A * rhs.E + C * rhs.F + E, B * rhs.E + D * rhs.F + F);
    public PdfMatrix Translate(double x, double y) => Multiply(new(1, 0, 0, 1, x, y));
    public static PdfMatrix From(IEnumerable<PdfValue> values)
    {
        var n = values.Select(PdfPrimitives.Number).ToArray();
        if (n.Length != 6)
        {
            throw new InvalidDataException("Expected six matrix components.");
        }

        return new(n[0], n[1], n[2], n[3], n[4], n[5]);
    }
}
