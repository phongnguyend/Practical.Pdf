using PdfObjectKit.Options;

namespace PdfObjectKit.Converters;

public sealed class PdfMarkdownOptions
{
    public PdfHeuristicOptions Heuristics { get; init; } = new();

    /// <summary>Optional body text height in PDF user space; otherwise estimated by character-weighted frequency.</summary>
    public double? BodyTextHeight { get; init; }

    /// <summary>Opt-in removal of hyphens between alphabetic wrapped words. Can alter legitimate compound words.</summary>
    public bool JoinHyphenatedWords { get; init; }
}
