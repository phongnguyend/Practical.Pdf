using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Validators;

namespace PdfObjectKit.Extractors;

/// <summary>Extracts terminal AcroForm fields, including inherited values and widget locations.</summary>
public static class PdfFormExtractor
{
    public static List<PdfFormField> Extract(PdfObjectDocument document)
    {
        PdfDocumentValidator.Validate(document);
        return new PdfFormGraph(document).Fields.Select(f => f.Model).ToList();
    }
}
