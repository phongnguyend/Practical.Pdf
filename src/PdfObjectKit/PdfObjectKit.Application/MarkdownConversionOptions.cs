using PdfObjectKit.Converters;
using PdfObjectKit.Options;
using PdfObjectKit.Models;

namespace PdfObjectKit.Application;

public sealed class MarkdownConversionOptions
{
    public bool IncludeImages { get; init; } = true;

    public bool JoinHyphenatedWords { get; init; }

    public double? BodyTextHeight { get; init; }

    public IReadOnlyList<PdfTableHint> TableHints { get; init; } = [];

    public PdfHeuristicOptions Heuristics { get; init; } = new();

    public PdfMarkdownOptions ToConverterOptions()
    {
        ArgumentNullException.ThrowIfNull(Heuristics);
        return new PdfMarkdownOptions
        {
            BodyTextHeight = BodyTextHeight,
            JoinHyphenatedWords = JoinHyphenatedWords,
            Heuristics = Heuristics,
            TableHints = TableHints,
            ImageRender = IncludeImages ? DefaultMarkDownImageRender.Render : (_, _, _) => string.Empty,
        };
    }
}
