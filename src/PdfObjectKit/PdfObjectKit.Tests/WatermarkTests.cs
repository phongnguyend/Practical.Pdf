using PdfObjectKit.Editors;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using PdfObjectKit.Converters;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Options;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class WatermarkTests
{
    public static void Run()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception(message);
            }
        }
        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original"), new FixturePage("Other")));
        var id = PdfWatermarkEditor.AddText(document, new() { Text = "DRAFT", PageNumbers = [1], FontSize = 30, Rotation = 0 });
        var roundTrip = PdfReader.Read(PdfWriter.Write(document));
        Check(PdfWatermarkEditor.GetWatermarks(roundTrip).Single().Id == id, "Watermark identity survives serialization");
        Check(PdfTextExtractor.Extract(roundTrip)[0].Text.Contains("DRAFT"), "Watermark paints text");
        Check(!PdfTextExtractor.Extract(roundTrip)[1].Text.Contains("DRAFT"), "Page selection honored");
        PdfWatermarkEditor.UpdateText(roundTrip, id, new() { Text = "FINAL", Layer = PdfWatermarkLayer.Background, Rotation = 90 });
        Check(PdfWatermarkEditor.GetWatermarks(roundTrip).Single().PageNumbers.Count == 2, "Update can change scope");
        Check(PdfTextExtractor.Extract(roundTrip).All(page => page.Text.Contains("FINAL") && !page.Text.Contains("DRAFT")), "Update replaces old placements");
        PdfPageEditor.InsertText(roundTrip, 1, "Later edit", 20, 40);
        Check(PdfWatermarkEditor.GetWatermarks(roundTrip).Single().PageNumbers.Count == 2, "Identity survives content consolidation");
        PdfWatermarkEditor.Remove(roundTrip, id);
        Check(PdfTextExtractor.Extract(roundTrip)[0].Text.Contains("Later edit") && !PdfTextExtractor.Extract(roundTrip)[0].Text.Contains("FINAL"), "Remove after content editing preserves later edits");
        var clean = PdfReader.Read(PdfWriter.Write(document));
        PdfWatermarkEditor.Remove(clean, id);
        Check(PdfWatermarkEditor.GetWatermarks(clean).Count == 0, "Remove deletes own placement streams");
        Check(PdfTextExtractor.Extract(clean)[0].Text.Contains("Original") && !PdfTextExtractor.Extract(clean)[0].Text.Contains("DRAFT"), "Remove preserves original content");
        var diagonal = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Selectable original")));
        PdfWatermarkEditor.AddText(diagonal, new() { Text = "DIAGONAL", Rotation = 45 });
        diagonal = PdfReader.Read(PdfWriter.Write(diagonal));
        var lines = PdfParagraphExtractor.Extract(diagonal, 1).SelectMany(paragraph => paragraph.Lines).ToList();
        Check(lines.Any(line => line.Text.Contains("Selectable original")) && lines.All(line => !line.Text.Contains("DIAGONAL")), "Diagonal watermark does not suppress original paragraph selections");
        Check(PdfTextExtractor.Extract(diagonal)[0].Text.Contains("DIAGONAL"), "Raw text extraction retains watermark");
        var before = PdfConverter.ToJson(document);
        try
        {
            PdfWatermarkEditor.UpdateText(document, id, new() { Opacity = 2 });
            throw new Exception("Invalid opacity accepted");
        }
        catch (ArgumentException)
        {
            Check(before == PdfConverter.ToJson(document), "Failed update is atomic");
        }
        foreach (var rotation in new[] { 0, 90, 180, 270 })
        {
            var rotated = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Page")));
            var page = new PdfGraph(rotated).Pages()[0].Dictionary;
            page.Entries!["CropBox"] = Array(Num(20), Num(30), Num(300), Num(400));
            page.Entries["Rotate"] = Num(rotation);
            PdfWatermarkEditor.AddText(rotated, new() { Text = "CENTER", Rotation = 0, FontSize = 20 });
            var text = PdfTextExtractor.Extract(PdfReader.Read(PdfWriter.Write(rotated)))[0];
            Check(text.Text.Contains("CENTER"), "Rotated cropped page retains watermark text");
        }
        Console.WriteLine("Text watermark creation, update, removal and geometry checks passed.");
    }
}
