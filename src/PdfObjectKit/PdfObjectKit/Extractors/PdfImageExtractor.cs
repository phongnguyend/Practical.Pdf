using PdfObjectKit.Validators;
using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
namespace PdfObjectKit.Extractors;

public static class PdfImageExtractor
{
    public static List<PdfPageImageIndex> Extract(PdfObjectDocument document)
    {
        PdfDocumentValidator.Validate(document);
        var graph = new PdfGraph(document);
        return graph.Pages().Select(page =>
        {
            var parsed = new PdfPageInterpreter(graph, page);
            return new PdfPageImageIndex
            {
                PageNumber = page.Number,
                Images = parsed.Images,
                Warning = parsed.ImageWarning
            };
        }).ToList();
    }
}
