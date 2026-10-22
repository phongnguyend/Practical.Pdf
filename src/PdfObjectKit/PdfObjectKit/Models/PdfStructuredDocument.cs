using PdfObjectKit.Diagnostics;

namespace PdfObjectKit.Models;

public sealed class PdfStructuredDocument
{
    public List<PdfContentPage> Pages { get; init; } = [];

    public List<PdfDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Compatibility projection of warning/error messages. Modify Diagnostics instead.</summary>
    public IReadOnlyList<string> Warnings => Diagnostics
        .Where(d => d.Severity >= PdfDiagnosticSeverity.Warning).Select(d => d.Message).ToArray();
}

public sealed class PdfContentPage
{
    public int PageNumber { get; init; }

    public List<PdfContentBlock> Blocks { get; init; } = [];
}

public enum PdfContentBlockKind
{
    Paragraph,
    Heading,
    ListItem,
    Table,
    Image
}

public sealed class PdfContentBlock
{
    public PdfContentBlockKind Kind { get; set; }

    public string Text { get; set; } = "";

    public int HeadingLevel { get; set; }

    public PdfTable? Table { get; set; }

    public PdfStructuredImage? Image { get; set; }

    public string? ListNumber { get; init; }

    public List<PdfParagraphLine> SourceLines { get; init; } = [];
}
