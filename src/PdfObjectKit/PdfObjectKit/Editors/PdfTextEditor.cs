namespace PdfObjectKit.Editors;

/// <summary>Replaces individual text drawing operands in a PDF object graph.</summary>
public static class PdfTextEditor
{
    /// <summary>
    /// Replaces the operand identified by an indexed run, validating its identity and
    /// stream checksum. Page numbers are one-based. An empty replacement deletes its text.
    /// The document is updated only on success; reacquire runs and objects after each edit.
    /// </summary>
    public static void UpdateText(PdfObjectDocument document, int pageNumber, PdfTextRun run,
        string text, PdfTextEditOptions? textOptions = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        Update(document, pageNumber, run.StreamObject, run.OperatorIndex, run.OperandIndex, text, textOptions, run);
    }

    /// <summary>
    /// Replaces an operand by its current stream object and zero-based operator index.
    /// Operand index is -1 for Tj, or the string's array index for TJ (including numeric entries).
    /// Use the run overload when a previously captured target needs stale-data validation.
    /// </summary>
    public static void UpdateText(PdfObjectDocument document, int pageNumber, PdfReference streamObject,
        int operatorIndex, int operandIndex, string text, PdfTextEditOptions? textOptions = null)
    {
        Update(document, pageNumber, streamObject, operatorIndex, operandIndex, text, textOptions, null);
    }

    private static void Update(PdfObjectDocument document, int pageNumber, PdfReference streamObject,
        int operatorIndex, int operandIndex, string text, PdfTextEditOptions? textOptions, PdfTextRun? expected)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(streamObject);
        ArgumentNullException.ThrowIfNull(text);
        if (operatorIndex < 0 || operandIndex < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(operatorIndex), "Invalid operator or operand index.");
        }

        PdfDocumentValidator.Validate(document);
        if (pageNumber < 1 || pageNumber > new PdfGraph(document).Pages().Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        // Re-index a copy of the actual streams, ignoring caller-supplied index text.
        var working = PdfReader.Read(PdfWriter.Write(document));
        var page = working.TextIndex[pageNumber - 1];
        if (page.Warning is not null || page.Runs is null)
        {
            throw new InvalidDataException($"Cannot edit page {pageNumber}: {page.Warning ?? page.RunWarning ?? "editable text runs unavailable."}");
        }

        var target = page.Runs.SingleOrDefault(r => r.StreamObject == streamObject
            && r.OperatorIndex == operatorIndex && r.OperandIndex == operandIndex);
        if (target is null)
        {
            throw new InvalidDataException("The specified object/operator/operand is not an editable text run on this page.");
        }

        if (expected is not null && expected.StreamSha256 != target.StreamSha256)
        {
            throw new InvalidDataException("The text run is stale. Reacquire it from the current document before editing.");
        }

        // Reuse glyph encoding, fallback embedding, advance compensation, and verification.
        var updated = PdfReader.Read(PdfTextRewriter.Apply(working, pageNumber, target, text, textOptions ?? new()));
        document.PdfVersion = updated.PdfVersion;
        document.Trailer = updated.Trailer;
        document.Objects = updated.Objects;
        document.TextIndex = updated.TextIndex;
        document.ImageIndex = updated.ImageIndex;
    }
}
