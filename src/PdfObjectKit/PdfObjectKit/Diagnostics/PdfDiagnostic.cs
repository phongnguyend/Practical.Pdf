using PdfObjectKit.Models;

namespace PdfObjectKit.Diagnostics;

/// <summary>A conversion diagnostic with optional source context; not an exception or a strict-mode policy.</summary>
public sealed record PdfDiagnostic
{
    public required PdfDiagnosticCode Code { get; init; }

    public required PdfDiagnosticSeverity Severity { get; init; }

    public required string Message { get; init; }

    /// <summary>One-based page number, or null for document-wide diagnostics.</summary>
    public int? PageNumber { get; init; }

    /// <summary>Related objects, not necessarily the exact object responsible for the problem.</summary>
    public IReadOnlyList<PdfReference> SourceObjects { get; init; } = [];

    /// <summary>Optional location in unrotated PDF user space.</summary>
    public PdfImagePoint? Location { get; init; }
}
