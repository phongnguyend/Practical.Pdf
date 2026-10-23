using PdfObjectKit.Fonts;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using System.Security.Cryptography;

namespace PdfObjectKit.Interpreters;

internal sealed record NativeSlot(int Operation, int Token, int Start, string Text, byte[] Bytes, PdfNativeFont Font, List<NativeGlyph> Glyphs, double FontSize, PdfReference Stream)
{
    public PdfMatrix TextMatrix { get; init; } = PdfMatrix.Identity;

    public double HorizontalScale { get; init; } = 1;

    public double Rise { get; init; }

    public int? FormOccurrenceId { get; init; }

    public double CharacterSpacing { get; init; }

    public double WordSpacing { get; init; }

    public PdfImagePoint BaselineStart { get; init; } = new(0, 0);

    public PdfImagePoint BaselineEnd { get; set; } = new(0, 0);

    public double Height { get; init; }

    public bool IsHorizontal { get; init; }
}
