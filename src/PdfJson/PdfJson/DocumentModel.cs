namespace Practical.PdfJson;

public sealed class DocumentModel
{
    public int SchemaVersion { get; set; } = 1;

    public List<PageModel> Pages { get; set; } = [];

    public string? OriginalPdfBase64 { get; set; }
}

public sealed class PageModel
{
    public double Width { get; set; } = 595;

    public double Height { get; set; } = 842;

    public List<TextModel> Text { get; set; } = [];
}

// Coordinates and font size are in PDF points (1/72 inch).
// X and Y specify the baseline's start, measured from the bottom-left.
public sealed class TextModel
{
    public string Value { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    public double FontSize { get; set; } = 12;
}
