namespace PdfObjectKit.Models;

/// <summary>A table region on a one-based page, in unrotated PDF user space.</summary>
public sealed class PdfTableHint
{
    public int PageNumber { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public bool FirstRowIsHeader { get; init; } = true;
}
