namespace PdfObjectKit.Models;

public sealed class PdfTable
{
    public List<PdfTableRow> Rows { get; init; } = [];
}

public sealed class PdfTableRow
{
    public bool IsHeader { get; set; }

    public List<PdfTableCell> Cells { get; init; } = [];
}

public sealed class PdfTableCell
{
    public string Text { get; set; } = "";

    public int RowSpan { get; set; } = 1;

    public int ColumnSpan { get; set; } = 1;

    public List<PdfParagraphLine> SourceLines { get; init; } = [];
}
