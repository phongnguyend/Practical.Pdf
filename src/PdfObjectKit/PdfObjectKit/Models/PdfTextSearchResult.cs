namespace PdfObjectKit.Models;

/// <summary>Highlight corners in unrotated PDF user coordinates, in perimeter order.</summary>
public sealed record PdfSearchRegion(IReadOnlyList<PdfImagePoint> Corners);

public sealed record PdfTextSearchMatch(int PageNumber, int Start, string Text, string Snippet, IReadOnlyList<PdfSearchRegion> Regions);

public sealed record PdfTextSearchResult(IReadOnlyList<PdfTextSearchMatch> Matches, bool Truncated, IReadOnlyList<string> Warnings);
