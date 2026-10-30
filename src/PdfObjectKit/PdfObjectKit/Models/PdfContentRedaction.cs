namespace PdfObjectKit.Models;

/// <summary>Redaction rectangle in unrotated PDF user space, using a one-based page number.</summary>
public sealed record PdfContentRedaction(int PageNumber, double X, double Y, double Width, double Height);
