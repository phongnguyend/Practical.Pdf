namespace PdfObjectKit.Models;

/// <summary>An image placeholder. Source bytes remain in the PDF object graph; no asset is exported.</summary>
public sealed class PdfStructuredImage
{
    public string? FileName { get; set; }

    public string ContentType { get; set; } = "application/octet-stream";

    public string? Caption { get; set; }

    public string? AltText { get; set; }

    /// <summary>Text recognized by a caller-provided OCR service. Not populated by PdfObjectKit.</summary>
    public string? OcrText { get; set; }

    /// <summary>Image interpretation supplied by a caller-provided AI or other analysis service.</summary>
    public string? ImageUnderstandingText { get; set; }

    /// <summary>Hash of encoded PDF image bytes, used for a deterministic fallback name.</summary>
    public string EncodedDataSha256 { get; init; } = "";

    public PdfImageOccurrence? Source { get; init; }
}
