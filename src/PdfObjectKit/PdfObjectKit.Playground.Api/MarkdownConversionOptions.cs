using PdfObjectKit.Converters;
using PdfObjectKit.Options;

internal sealed class MarkdownConversionOptions
{
    public bool IncludeImages { get; init; } = true;

    public bool JoinHyphenatedWords { get; init; }

    public double? BodyTextHeight { get; init; }

    public PdfHeuristicOptions Heuristics { get; init; } = new();

    public PdfMarkdownOptions ToConverterOptions()
    {
        ArgumentNullException.ThrowIfNull(Heuristics);
        return new PdfMarkdownOptions
        {
            BodyTextHeight = BodyTextHeight,
            JoinHyphenatedWords = JoinHyphenatedWords,
            Heuristics = Heuristics,
            ImageRender = IncludeImages ? DefaultMarkDownImageRender.Render : (_, _, _) => string.Empty,
        };
    }
}
