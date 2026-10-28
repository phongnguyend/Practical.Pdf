using PdfObjectKit.Converters;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Rendering;
using PdfObjectKit.Writers;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class AnnotationTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok)
            {
                throw new Exception("Annotations: " + message);
            }
        }
        var document = PdfReader.Read(PdfFixture.Create(null, new("Original text"), new("Second page")));
        foreach (var kind in Enum.GetValues<PdfAnnotationKind>())
        {
            PdfAnnotationEditor.Add(document, 1, kind, kind == PdfAnnotationKind.Comment
                ? [new(30, 30, 24, 24)] : [new(40, 100, 120, 15), new(40, 80, 80, 15)], "Ghi chú 日本語", "Reviewer");
        }
        document = PdfReader.Read(PdfWriter.Write(document));
        var annotations = PdfAnnotationExtractor.Extract(document);
        Check(annotations.Count == 4 && annotations.All(a => a.Contents == "Ghi chú 日本語" && a.Author == "Reviewer"), "Unicode comments round-trip");
        Check(annotations.Where(a => a.Kind != PdfAnnotationKind.Comment).All(a => a.QuadPoints.Count == 16), "multiple lines retain their quads");
        Check(PdfTextExtractor.Extract(document)[0].Text == "Original text", "markup leaves underlying content intact");
        var before = PdfConverter.ToJson(document);
        var display = PdfCanvasExporter.Export(document, 1);
        Check(display.Commands.Count(c => c.Kind == "l") >= 7, "saved appearances reach native renderer");
        Check(before == PdfConverter.ToJson(document), "rendering does not mutate annotations");
        var extracted = PdfPageEditor.ExtractPages(document, [1]);
        Check(PdfAnnotationExtractor.Extract(extracted).Count == 4, "page extraction preserves annotations");
        Check(PdfAnnotationExtractor.Extract(PdfPageEditor.ExtractPages(document, [2])).Count == 0, "other pages stay unchanged");
        using (var independent = NativeTestDocument.Open(PdfWriter.Write(extracted)))
        {
            Check(independent.GetPage(1).Text == "Original text", "independent PDF read succeeds");
        }
        PdfAnnotationEditor.UpdateContents(document, 1, 1, "Updated note");
        Check(PdfAnnotationExtractor.Extract(PdfReader.Read(PdfWriter.Write(document)))[0].Contents == "Updated note", "comments can be updated");
        PdfAnnotationEditor.Delete(document, 1, 2);
        Check(PdfAnnotationExtractor.Extract(document).Select(a => a.Kind).SequenceEqual(new[] { PdfAnnotationKind.Comment, PdfAnnotationKind.Underline, PdfAnnotationKind.StrikeOut }), "delete removes only the chosen annotation");
        before = PdfConverter.ToJson(document);
        try
        {
            PdfAnnotationEditor.Add(document, 1, PdfAnnotationKind.Highlight, [new(0, 0, double.NaN, 20)]);
            throw new Exception("Expected invalid region rejection.");
        }
        catch (ArgumentException)
        {
            Check(before == PdfConverter.ToJson(document), "invalid insertion is atomic");
        }
        var graph = new PdfGraph(document);
        graph.Resolve(graph.Get(graph.Pages()[0].Dictionary, "Annots")!.Items![0]).Entries!["F"] = Num(128);
        try
        {
            PdfAnnotationEditor.Delete(document, 1, 1);
            throw new Exception("Expected locked annotation rejection.");
        }
        catch (NotSupportedException)
        {
            Check(PdfAnnotationExtractor.Extract(document)[0].IsReadOnly, "locked annotation remains");
        }
        Console.WriteLine("Annotation creation, appearance, editing and deletion checks passed.");
    }
}
