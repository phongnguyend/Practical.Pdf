namespace PdfObjectKit.Models;

public enum PdfAnnotationKind
{
    Comment,
    Highlight,
    Underline,
    StrikeOut
}

/// <summary>A rectangle in unrotated PDF page coordinates (points, origin at bottom left).</summary>
public sealed record PdfAnnotationRegion(double X, double Y, double Width, double Height);

public sealed class PdfAnnotation
{
    public int PageNumber { get; init; }

    /// <summary>One-based position in the page's Annots array. Re-extract after editing.</summary>
    public int AnnotationNumber { get; init; }

    public PdfAnnotationKind Kind { get; init; }

    public string Contents { get; init; } = "";

    public string Author { get; init; } = "";

    public bool IsReadOnly { get; init; }

    public List<double> Rectangle { get; init; } = [];

    public List<double> QuadPoints { get; init; } = [];
}
