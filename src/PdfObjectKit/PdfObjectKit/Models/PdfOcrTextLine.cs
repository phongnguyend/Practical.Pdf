namespace PdfObjectKit.Models;

/// <summary>A corrected OCR line in normalized image coordinates, with a top-origin baseline.</summary>
public sealed record PdfOcrTextLine(string Text, double X, double Y, double Width, double Height, double Baseline);
