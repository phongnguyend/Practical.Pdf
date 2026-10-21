using PdfObjectKit.Diagnostics;
using PdfObjectKit.Models;

namespace PdfObjectKit.Converters;

public sealed class PdfMarkdownResult
{
    public required string Markdown { get; init; }

    public required PdfStructuredDocument Document { get; init; }

    public IReadOnlyList<PdfDiagnostic> Diagnostics => Document.Diagnostics.AsReadOnly();

    public IReadOnlyList<string> Warnings => Document.Warnings;
}
