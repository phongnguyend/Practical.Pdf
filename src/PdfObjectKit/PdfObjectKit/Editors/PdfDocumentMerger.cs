using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Options;

namespace PdfObjectKit.Editors;

/// <summary>Combines page content into a new, independent document.</summary>
public static class PdfDocumentMerger
{
    /// <summary>Appends all pages in source order. Inputs are never modified.
    /// Preserves supported forms, navigation and document resources. Conflicting names and unsupported advanced features are rejected.</summary>
    public static PdfObjectDocument Merge(IEnumerable<PdfObjectDocument> documents, PdfPageAssemblyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var copier = new PdfPageCopier(options, cancellationToken);
        var count = 0;
        foreach (var source in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            ArgumentNullException.ThrowIfNull(source);
            copier.Append(source, Enumerable.Range(1, new PdfGraph(source).Pages().Count).ToArray());
        }
        if (count == 0)
        {
            throw new ArgumentException("Provide at least one document.", nameof(documents));
        }
        return copier.Finish();
    }
}
