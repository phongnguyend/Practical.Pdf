using PdfObjectKit.Options;
using PdfObjectKit.Models;

namespace PdfObjectKit.Converters;

public sealed class PdfMarkdownOptions
{
    /// <summary>
    /// Renders an image using its enriched metadata, one-based page number, and image occurrence number.
    /// Return Markdown without surrounding block separators, or an empty string to omit the image.
    /// Custom output is emitted verbatim; the caller is responsible for escaping it.
    /// </summary>
    public Func<PdfStructuredImage, int, int, string> ImageRender { get; init; } = DefaultMarkDownImageRender.Render;

    /// <summary>Explicit table regions in unrotated PDF coordinates. Regions must not overlap.</summary>
    public IReadOnlyList<PdfTableHint> TableHints { get; init; } = [];

    public PdfHeuristicOptions Heuristics { get; init; } = new();

    /// <summary>Optional body text height in PDF user space; otherwise estimated by character-weighted frequency.</summary>
    public double? BodyTextHeight { get; init; }

    /// <summary>Opt-in removal of hyphens between alphabetic wrapped words. Can alter legitimate compound words.</summary>
    public bool JoinHyphenatedWords { get; init; }
}
