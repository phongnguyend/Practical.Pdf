namespace PdfObjectKit.Models;

/// <summary>Conservative page-space bounds of a glyph, image placement or painted path removed by content redaction.</summary>
public sealed record PdfRedactionImpact(int PageNumber, string Kind, double X, double Y, double Width, double Height);
