using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Rendering;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class StreamBoundaryTests
{
    public static void Run()
    {
        const string content = "BT /F1 12 Tf 1 0 0 1 20 100 Tm [(Boundary) 0 ( text)] TJ ET";
        foreach (var split in new[] { content.IndexOf(" TJ", StringComparison.Ordinal), content.IndexOf("Boundary", StringComparison.Ordinal) + 3, content.IndexOf("( text)", StringComparison.Ordinal) + 2 })
        {
            var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage()));
            var page = new PdfGraph(document).Pages()[0];
            PdfValue Add(string text)
            {
                var id = new PdfReference(document.Objects.Max(item => item.Id.ObjectNumber) + 1);
                document.Objects.Add(new(id, Stream(Encoding.ASCII.GetBytes(text))));
                return Ref(id);
            }
            var first = Add(content[..split]);
            var second = Add(content[split..]);
            page.Dictionary.Entries!["Contents"] = Array(first, second);
            var before = PdfConverter.ToJson(document);
            var text = PdfTextExtractor.Extract(document)[0];
            var selections = PdfParagraphExtractor.ExtractSelectionLines(document, 1);
            if (text.Warning is not null || text.Text != "Boundary text" || selections.Single().Text != "Boundary text"
                || !PdfMarkdownConverter.Convert(document).Markdown.Contains("Boundary text")
                || PdfTextSearcher.Search(document, "Boundary text").Matches.Count != 1)
            {
                throw new Exception("Text must be parsed across stream operand and string boundaries.");
            }
            var display = PdfCanvasExporter.Export(document, 1);
            var renderedText = string.Concat(display.Commands.Where(command => command.Kind == "text").SelectMany(command => command.Glyphs!).Select(glyph => glyph.Text));
            if (renderedText != "Boundary text")
            {
                throw new Exception("Native rendering must use the same logical streams and operator identities as text extraction.");
            }
            if (PdfConverter.ToJson(document) != before)
            {
                throw new Exception("Extraction must not consolidate or mutate source PDF streams.");
            }
            var hash = selections[0].Runs[0].StreamSha256;
            document.Objects.Single(item => item.Id == second.Reference).Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content[split..] + "\n "));
            if (PdfParagraphExtractor.ExtractSelectionLines(document, 1)[0].Runs[0].StreamSha256 == hash)
            {
                throw new Exception("Logical content checksums must cover later source streams too.");
            }
            document.Objects.Single(item => item.Id == second.Reference).Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(" "));
            if (split < content.IndexOf("TJ", StringComparison.Ordinal) && PdfTextExtractor.Extract(document)[0].Warning is null)
            {
                throw new Exception("Truly incomplete content must still report a warning.");
            }
        }
        Console.WriteLine("Cross-stream extraction, selection, Markdown, immutability and checksum checks passed.");
    }
}
