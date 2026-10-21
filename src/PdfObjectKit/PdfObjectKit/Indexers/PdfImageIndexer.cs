using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
namespace PdfObjectKit.Indexers;

internal static class PdfImageIndexer
{
    public static List<PdfPageImageIndex> Build(PdfObjectDocument document)
    {
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
