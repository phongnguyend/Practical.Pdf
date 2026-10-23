using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

internal static class PdfTextDeleter
{
    public static void Delete(PdfObjectDocument document, int pageNumber, IReadOnlyList<PdfTextRun> runs)
    {
        PdfTextRunRewriter.Apply(document, pageNumber, runs, (_, _, _, _) => new PdfTextRewriteActions(
            (_, _, _) => PdfTextRewritePlan.Empty,
            (_, _) => string.Empty,
            (_, slot, _, _) => PreserveAdvance(slot)));
    }

    private static string PreserveAdvance(NativeSlot slot)
    {
        var spacing = slot.Glyphs.Count * slot.CharacterSpacing + slot.Glyphs.Count(glyph => glyph.Bytes.Length == 1 && glyph.Bytes[0] == 32) * slot.WordSpacing;
        if (slot.FontSize == 0 && spacing != 0)
        {
            throw new NotSupportedException("Cannot preserve spaced text advance at zero font size.");
        }
        var advance = slot.Glyphs.Sum(glyph => glyph.Width) + (slot.FontSize == 0 ? 0 : spacing / slot.FontSize);
        return $"[{Syntax(Num(-advance * 1000))}] TJ\n";
    }
}
