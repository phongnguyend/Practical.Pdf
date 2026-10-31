namespace PdfObjectKit.Models;

/// <summary>Selectable text with page-space geometry, including rotated or skewed runs.</summary>
public sealed record PdfTextSelection(string Text, IReadOnlyList<PdfImagePoint> Corners, List<PdfTextRun> Runs);
