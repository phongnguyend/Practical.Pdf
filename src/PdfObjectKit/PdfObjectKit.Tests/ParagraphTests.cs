using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Extractors;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;

internal static class ParagraphTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Paragraph detection: " + name);
            }

            checks++;
        }

        PdfObjectDocument Fixture(string content)
        {
            var doc = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
            var id = PdfTextExtractor.Extract(doc)[0].ContentStreams.Single();
            doc.Objects.Single(o => o.Id == id).Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content));
            return doc;
        }

        var document = Fixture("BT /F1 12 Tf 1 0 0 1 20 700 Tm (Hello) Tj 1 0 0 1 52 700 Tm (world) Tj 1 0 0 1 20 685 Tm (Next line) Tj 1 0 0 1 20 640 Tm (New paragraph) Tj ET");
        var before = PdfConverter.ToJson(document);
        var paragraphs = PdfParagraphExtractor.Extract(document, 1);
        Check(paragraphs.Count == 2, "vertical gap separates paragraphs");
        Check(paragraphs[0].Text == "Hello world\nNext line", "multiple operands and lines grouped with inferred spaces");
        Check(paragraphs[0].Lines[0].Runs.Count == 2, "source operands retained");
        Check(paragraphs[0].Lines[0].Runs[0].StreamObject == PdfTextExtractor.Extract(document)[0].ContentStreams[0], "source object identity retained");
        Check(PdfConverter.ToJson(document) == before, "detection does not mutate graph or indexes");
        Check(paragraphs[0].Lines[0].Baseline == 700, "baseline coordinates retained");

        document = Fixture("BT /F1 12 Tf 1 0 0 1 20 700 Tm (Left column) Tj 1 0 0 1 300 700 Tm (Right column) Tj 1 0 0 1 20 685 Tm (Left next) Tj 1 0 0 1 300 685 Tm (Right next) Tj ET");
        paragraphs = PdfParagraphExtractor.Extract(document, 1);
        Check(paragraphs.Count == 2 && paragraphs.All(p => p.Lines.Count == 2), "separated columns remain separate");
        document = Fixture("BT /F1 20 Tf 1 0 0 1 20 700 Tm (Heading) Tj /F1 12 Tf 1 0 0 1 20 680 Tm (Body) Tj ET");
        Check(PdfParagraphExtractor.Extract(document, 1).Count == 2, "font size separates heading");
        document = Fixture("");
        Check(PdfParagraphExtractor.Extract(document, 1).Count == 0, "blank page");
        document = Fixture("BT /F1 12 Tf 0 1 -1 0 20 700 Tm (Rotated) Tj ET");
        try
        {
            PdfParagraphExtractor.Extract(document, 1);
            throw new Exception("Rotated text accepted");
        }
        catch (NotSupportedException)
        {
            checks++;
        }

        document = Fixture("BT /F1 12 Tf 1 0 0 1 20 700 Tm (First) Tj ET");
        var page = document.Objects.Single(o => o.Value.Entries?.GetValueOrDefault("Type")?.Name == "Page").Value;
        var firstStream = page.Entries!["Contents"];
        var secondId = new PdfReference(document.Objects.Max(o => o.Id.ObjectNumber) + 1);
        document.Objects.Add(new(secondId, new PdfValue
        {
            Type = "stream",
            Entries = [],
            Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("BT /F1 12 Tf 1 0 0 1 20 685 Tm (Second) Tj ET"))
        }));
        page.Entries["Contents"] = new PdfValue { Type = "array", Items = [firstStream, new() { Type = "reference", Reference = secondId }] };
        paragraphs = PdfParagraphExtractor.Extract(document, 1);
        Check(paragraphs.Count == 1 && paragraphs[0].Lines.Count == 2, "groups across content stream objects");
        Check(paragraphs[0].Lines[1].Runs[0].StreamObject == secondId, "second stream reference preserved");
        Check(PdfParagraphExtractor.Extract(PdfReader.Read(PdfWriter.Write(document)), 1)[0].Text == "First\nSecond", "round trip detection");
        var indexed = PdfReader.Read(PdfWriter.Write(document));
        var json = PdfConverter.ToJson(indexed);
        Check(!json.Contains("\"paragraphs\""), "main JSON excludes paragraphs");
        var separate = System.Text.Json.Nodes.JsonNode.Parse(PdfConverter.ToParagraphsJson(indexed))!;
        Check(separate["pages"]![0]!["paragraphs"]![0]!["text"]!.GetValue<string>() == "First\nSecond", "separate JSON contains paragraph text");
        Check(separate["pages"]![0]!["paragraphs"]![0]!["lines"]![1]!["runs"]![0]!["streamObject"]!["objectNumber"]!.GetValue<long>() == secondId.ObjectNumber, "source reference exported");
        Check(PdfConverter.ToJson(indexed) == json, "separate export leaves main document unchanged");
        Check(PdfConverter.FromJson(json).Objects.Count == indexed.Objects.Count, "main JSON round trip");
        var rotated = Fixture("BT /F1 12 Tf 0 1 -1 0 20 700 Tm (Rotated) Tj ET");
        var rotatedJson = System.Text.Json.Nodes.JsonNode.Parse(PdfConverter.ToParagraphsJson(rotated))!;
        Check(rotatedJson["pages"]![0]!["warning"] is not null, "unsupported page exports warning");
        Check(rotatedJson["pages"]![0]!["paragraphs"] is null, "unsupported page omits paragraphs");
        var blankJson = System.Text.Json.Nodes.JsonNode.Parse(PdfConverter.ToParagraphsJson(Fixture("")))!;
        Check(blankJson["pages"]![0]!["paragraphs"]!.AsArray().Count == 0, "blank page exports empty paragraphs");
        Check(PdfReader.Read(PdfWriter.Write(document)).Objects.Count == document.Objects.Count, "reader preserves objects without indexing");
        Console.WriteLine($"Passed {checks} paragraph detection checks.");
    }
}
