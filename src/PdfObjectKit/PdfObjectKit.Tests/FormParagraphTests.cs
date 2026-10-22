using System.Security.Cryptography;
using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class FormParagraphTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Form paragraphs: " + message);
            }

            checks++;
        }

        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
        var page = new PdfGraph(document).Pages()[0];
        PdfReference AddForm(string content, PdfValue resources, double scale = 1)
        {
            var id = new PdfReference(document.Objects.Max(o => o.Id.ObjectNumber) + 1);
            document.Objects.Add(new(id, new PdfValue
            {
                Type = "stream",
                Entries = new()
                {
                    ["Type"] = Name("XObject"), ["Subtype"] = Name("Form"),
                    ["BBox"] = new() { Type = "array", Items = [Num(0), Num(0), Num(200), Num(100)] },
                    ["Matrix"] = new() { Type = "array", Items = [Num(scale), Num(0), Num(0), Num(scale), Num(5), Num(10)] },
                    ["Resources"] = resources
                },
                Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content))
            }));
            return id;
        }

        const string formText = "BT /F1 12 Tf 10 20 Td (Reusable text) Tj ET";
        var form = AddForm(formText, new PdfValue { Type = "dictionary", Entries = new() { ["Font"] = page.Resources.Entries!["Font"] } });
        var outer = AddForm("/Inner Do", new PdfValue
        {
            Type = "dictionary",
            Entries = new() { ["XObject"] = new() { Type = "dictionary", Entries = new() { ["Inner"] = Ref(form) } } }
        }, 2);
        page.Resources.Entries!["XObject"] = new PdfValue
        {
            Type = "dictionary", Entries = new() { ["Shared"] = Ref(form), ["Outer"] = Ref(outer) }
        };
        const string content = "BT /F1 12 Tf 20 750 Td (Page text) Tj ET "
            + "q 1 0 0 1 20 600 cm /Shared Do Q "
            + "q 1 0 0 1 300 500 cm /Shared Do Q "
            + "q 1 0 0 1 20 300 cm /Outer Do Q";
        page.Streams[0].Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content));
        var before = PdfConverter.ToJson(document);
        var paragraphs = PdfParagraphExtractor.Extract(document, 1);
        var lines = paragraphs.SelectMany(p => p.Lines).ToList();
        var formLines = lines.Where(l => l.Text == "Reusable text").ToList();
        Check(formLines.Count == 3, "all repeated and nested placements extracted");
        Check(lines.Any(l => l.Text == "Page text" && l.Runs[0].FormOccurrenceId is null), "page text preserved");
        Check(formLines.Select(l => l.Runs[0].FormOccurrenceId).SequenceEqual(new int?[] { 1, 2, 4 }), "nested and repeated invocation identities");
        Check(formLines.All(l => l.Runs[0].StreamObject == form), "original shared stream retained");
        Check(formLines.All(l => l.Runs[0].OperatorIndex == 3), "Form-local operator indices");
        Check(formLines.All(l => l.Runs[0].StreamSha256 == Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(formText)))), "Form stream hash");
        Check(formLines[0].Left == 35 && formLines[0].Baseline == 630, "translated coordinates");
        Check(formLines[2].Left == 55 && formLines[2].Baseline == 370 && formLines[2].Height == 24, "nested scaled coordinates");
        var result = PdfMarkdownConverter.Convert(document);
        Check(result.Diagnostics.Count == 0 && result.Markdown.Contains("Page text") && result.Markdown.Split("Reusable text").Length == 4, "Markdown includes page and Form text");
        Check(PdfConverter.ToParagraphsJson(document).Contains("\"formOccurrenceId\": 4"), "JSON retains occurrence identity");
        Check(PdfConverter.ToJson(document) == before, "extraction leaves graph unchanged");
        var roundTrip = PdfParagraphExtractor.Extract(PdfReader.Read(PdfWriter.Write(document)), 1);
        Check(roundTrip.SelectMany(p => p.Lines).SelectMany(l => l.Runs).Select(r => r.FormOccurrenceId)
            .SequenceEqual(lines.SelectMany(l => l.Runs).Select(r => r.FormOccurrenceId)), "stable occurrence IDs after round trip");
        try
        {
            PdfTextEditor.UpdateText(document, 1, formLines[0].Runs[0], "Changed");
            throw new Exception("Shared Form edit accepted");
        }
        catch (NotSupportedException)
        {
            Check(PdfConverter.ToJson(document) == before, "Form edit rejected without mutation");
        }

        page.Streams[0].Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("q 0 1 -1 0 100 100 cm /Shared Do Q"));
        try
        {
            PdfParagraphExtractor.Extract(document, 1);
            throw new Exception("Rotated Form accepted");
        }
        catch (NotSupportedException)
        {
            checks++;
        }

        Console.WriteLine($"Passed {checks} Form paragraph checks.");
    }
}
