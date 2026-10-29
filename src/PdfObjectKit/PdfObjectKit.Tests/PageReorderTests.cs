using PdfObjectKit.Converters;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class PageReorderTests
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Page reorder: " + message);
            }
        }
        var source = PdfReader.Read(PdfFixture.Create(null, new FixturePage("First"), new FixturePage("Second"), new FixturePage("Third")));
        PdfFormEditor.InsertTextField(source, 3, "Name", "Value", 20, 20, 100, 20);
        PdfAnnotationEditor.Add(source, 2, PdfAnnotationKind.Comment, [new(20, 30, 20, 20)], "Note", "Reviewer");
        PdfMetadataEditor.Update(source, new() { Fields = new() { ["Title"] = "Reorder test" } });
        PdfPageEditor.RotatePage(source, 3, 90);
        var graph = new PdfGraph(source);
        var pageIds = graph.Resolve(graph.Resolve(source.Trailer.Entries!["Root"]).Entries!["Pages"]).Entries!["Kids"]!.Items!;
        var link = Dict(("Type", Name("Annot")), ("Subtype", Name("Link")), ("Rect", Array(Num(20), Num(40), Num(100), Num(60))), ("Dest", Array(pageIds[2], Name("Fit"))));
        var linkId = new PdfReference(source.Objects.Max(item => item.Id.ObjectNumber) + 1);
        source.Objects.Add(new(linkId, link));
        graph.Pages()[0].Dictionary.Entries!["Annots"] = Array(Ref(linkId));
        var before = PdfConverter.ToJson(source);
        var reordered = PdfReader.Read(PdfWriter.Write(PdfPageEditor.ReorderPages(source, [3, 1, 2])));
        Check(PdfTextExtractor.Extract(reordered).Select(page => page.Text).SequenceEqual(new[] { "Third", "First", "Second" }), "content order");
        Check(PdfConverter.ToJson(source) == before, "source unchanged");
        Check(PdfFormExtractor.Extract(reordered).Single().Name == "Name", "field name retained");
        Check(PdfFormExtractor.Extract(reordered).Single().Widgets.Single().PageNumber == 1, "field follows page");
        Check(PdfAnnotationExtractor.Extract(reordered).Single().PageNumber == 3, "comment follows page");
        Check(PdfMetadataExtractor.Extract(reordered).Values["Title"] == "Reorder test", "metadata retained");
        graph = new PdfGraph(reordered);
        Check(graph.Number(graph.Pages()[0].Dictionary, "Rotate") == 90, "rotation follows page");
        var movedLink = graph.Resolve(graph.Get(graph.Pages()[1].Dictionary, "Annots")!.Items![0]);
        Check(ReferenceEquals(graph.Resolve(graph.Get(movedLink, "Dest")!.Items![0]), graph.Pages()[0].Dictionary), "link still targets original third page");
        using (var independent = NativeTestDocument.Open(PdfWriter.Write(reordered)))
        {
            Check(independent.NumberOfPages == 3, "independent reader accepts result");
        }
        foreach (var invalid in new int[][] { [], [1, 2], [1, 1, 3], [0, 2, 3], [1, 2, 4], [1, 2, 3, 4] })
        {
            try
            {
                PdfPageEditor.ReorderPages(source, invalid);
                throw new Exception("Invalid page permutation accepted");
            }
            catch (ArgumentException)
            {
                Check(PdfConverter.ToJson(source) == before, "invalid reorder leaves source unchanged");
            }
        }
        var identity = PdfPageEditor.ReorderPages(source, [1, 2, 3]);
        Check(PdfTextExtractor.Extract(identity)[0].Text == "First", "identity permutation");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try
        {
            PdfPageEditor.ReorderPages(source, [3, 2, 1], cancellationToken: cancel.Token);
            throw new Exception("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            // No partial change is published.
        }
        Console.WriteLine("Page reorder validation, content, forms, annotations, links and source isolation passed.");
    }
}
