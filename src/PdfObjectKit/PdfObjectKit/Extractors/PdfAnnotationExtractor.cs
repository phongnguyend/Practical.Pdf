using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Extractors;

public static class PdfAnnotationExtractor
{
    public static List<PdfAnnotation> Extract(PdfObjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var graph = new PdfGraph(document);
        var result = new List<PdfAnnotation>();
        var pages = graph.Pages();
        for (var page = 0; page < pages.Count; page++)
        {
            var annotations = graph.Get(pages[page].Dictionary, "Annots")?.Items ?? [];
            for (var index = 0; index < annotations.Count; index++)
            {
                var node = graph.Resolve(annotations[index]);
                var subtype = graph.Get(node, "Subtype")?.Name;
                if (subtype is not ("Text" or "Highlight" or "Underline" or "StrikeOut"))
                {
                    continue;
                }
                result.Add(new PdfAnnotation
                {
                    PageNumber = page + 1,
                    AnnotationNumber = index + 1,
                    Kind = subtype == "Text" ? PdfAnnotationKind.Comment : Enum.Parse<PdfAnnotationKind>(subtype),
                    Contents = graph.Get(node, "Contents") is { } contents ? PdfFormGraph.Text(contents) : "",
                    Author = graph.Get(node, "T") is { } author ? PdfFormGraph.Text(author) : "",
                    IsReadOnly = ((int)graph.Number(node, "F") & (64 | 128 | 512)) != 0,
                    Rectangle = graph.Get(node, "Rect")?.Items?.Select(item => Number(graph.Resolve(item))).ToList() ?? [],
                    QuadPoints = graph.Get(node, "QuadPoints")?.Items?.Select(item => Number(graph.Resolve(item))).ToList() ?? []
                });
            }
        }
        return result;
    }
}
