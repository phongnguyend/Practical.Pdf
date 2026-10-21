using System.Text;
using System.Text.Json;
using PdfObjectKit.Analysis;
using PdfObjectKit.Converters;
using PdfObjectKit.Extractors;
using PdfObjectKit.Models;
using PdfObjectKit.Options;
using PdfObjectKit.Readers;

internal static class HeuristicTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Heuristics: " + name);
            }

            checks++;
        }

        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
        var stream = PdfTextExtractor.Extract(document)[0].ContentStreams.Single();
        void Content(string content)
        {
            document.Objects.Single(o => o.Id == stream).Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content));
        }

        Content("BT /F1 16 Tf 20 700 Td (Heading) Tj ET");
        Check(PdfDocumentAnalyzer.Analyze(document, new() { BodyTextHeight = 12 }).Pages[0].Blocks[0].Kind == PdfContentBlockKind.Heading, "default heading");
        Check(PdfDocumentAnalyzer.Analyze(document, new()
        {
            BodyTextHeight = 12,
            Heuristics = new() { HeadingSizeRatio = 1.5 }
        }).Pages[0].Blocks[0].Kind == PdfContentBlockKind.Paragraph, "custom heading ratio");

        Content("BT /F1 12 Tf 20 700 Td (First line) Tj 0 -18 Td (Second line) Tj ET");
        Check(PdfParagraphExtractor.Extract(document, 1).Count == 1, "default paragraph gap");
        var tighter = new PdfHeuristicOptions { MaximumLineGapFactor = 1.2 };
        Check(PdfParagraphExtractor.Extract(document, 1, tighter).Count == 2, "custom paragraph gap");
        using var json = JsonDocument.Parse(PdfConverter.ToParagraphsJson(document, tighter));
        Check(json.RootElement.GetProperty("pages")[0].GetProperty("paragraphs").GetArrayLength() == 2, "paragraph JSON honors options");
        Check(PdfMarkdownConverter.Convert(document).Markdown == PdfMarkdownConverter.Convert(document, new() { Heuristics = new() }).Markdown, "explicit defaults unchanged");

        PdfParagraphLine Line(string text, double x, double y) => new()
        {
            Text = text,
            Left = x,
            Right = x + 40,
            Baseline = y,
            Height = 12
        };
        var lines = new List<PdfParagraphLine>
        {
            Line("Item", 20, 700), Line("Price", 200, 700),
            Line("Apple", 20, 682), Line("10", 200, 682)
        };
        Check(PdfTableDetector.Detect(lines).Count == 0, "default conservative table evidence");
        Check(PdfTableDetector.Detect(lines, new() { MinimumTableRows = 2, MinimumNumericCells = 1 }).Count == 1, "custom table evidence");
        lines.AddRange([Line("Pear", 20, 664), Line("20", 200, 664)]);
        Check(PdfTableDetector.Detect(lines).Count == 1, "default three-row table");
        Check(PdfTableDetector.Detect(lines, new() { TableRowGapFactor = 1 }).Count == 0, "custom table row gap");
        Check(PdfLayoutAnalyzer.Analyze(lines, [], 1).Diagnostics.Count == 0, "default column separation");
        Check(PdfLayoutAnalyzer.Analyze(lines, [], 1, new() { ColumnGutterFactor = 20 }).Diagnostics.Count > 0, "custom gutter retains ambiguous reading-order diagnostic");

        foreach (var invalid in new PdfHeuristicOptions[]
        {
            new() { HeadingSizeRatio = double.NaN },
            new() { TableRowGapFactor = double.PositiveInfinity },
            new() { WordGapFactor = 0 },
            new() { ParagraphSizeRatio = 1.1 },
            new() { MinimumTableRows = 1 },
            new() { HeightRoundingDigits = -1 },
            new() { MinimumLineGapFactor = 2, MaximumLineGapFactor = 1 }
        })
        {
            var rejected = false;
            try
            {
                PdfMarkdownConverter.Convert(document, new() { Heuristics = invalid });
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            Check(rejected, "invalid options fail before analysis");
        }

        Console.WriteLine($"Passed {checks} configurable heuristic checks.");
    }
}
