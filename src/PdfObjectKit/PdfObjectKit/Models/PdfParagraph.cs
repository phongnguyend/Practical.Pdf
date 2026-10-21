namespace PdfObjectKit.Models;

/// <summary>A layout-derived paragraph. Source PDF objects are not changed.</summary>
public sealed class PdfParagraph
{
    public int PageNumber { get; init; }

    public List<PdfParagraphLine> Lines { get; init; } = [];

    public string Text => string.Join("\n", Lines.Select(line => line.Text));
}

public sealed class PdfParagraphLine
{
    public string Text { get; init; } = "";

    public double Left { get; init; }

    public double Right { get; init; }

    public double Baseline { get; init; }

    public double Height { get; init; }

    public List<PdfTextRun> Runs { get; init; } = [];
}
