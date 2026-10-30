using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Extractors;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;

internal static class TableHintTests
{
    public static void Run()
    {
        var doc = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
        var stream = PdfTextExtractor.Extract(doc)[0].ContentStreams.Single();
        var content = "BT /F1 12 Tf 1 0 0 1 20 250 Tm (Name) Tj 1 0 0 1 160 250 Tm (City) Tj "
            + "1 0 0 1 20 230 Tm (Alice) Tj 1 0 0 1 160 230 Tm (Paris) Tj 1 0 0 1 20 100 Tm (Outside) Tj ET";
        doc.Objects.Single(o => o.Id == stream).Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content));
        var before = PdfConverter.ToJson(doc);
        var hint = new PdfTableHint { PageNumber = 1, X = 10, Y = 220, Width = 230, Height = 50 };
        var automatic = PdfMarkdownConverter.Convert(doc);
        if (automatic.Document.Pages.SelectMany(page => page.Blocks).Any(block => block.Kind == PdfContentBlockKind.Table))
        {
            throw new Exception("Table hint fixture must not be automatically classified as a table.");
        }
        var result = PdfMarkdownConverter.Convert(doc, new PdfMarkdownOptions { TableHints = [hint] });
        var blocks = result.Document.Pages[0].Blocks;
        var table = blocks.Single(block => block.Kind == PdfContentBlockKind.Table).Table!;
        if (table.Rows.Count != 2 || table.Rows[0].Cells.Count != 2 || !table.Rows[0].IsHeader
            || table.Rows[1].Cells[1].Text != "Paris" || !result.Markdown.Contains("| Name | City |")
            || result.Markdown.Split("Alice").Length != 2 || !result.Markdown.Contains("Outside")
            || blocks.Where(block => block.Kind != PdfContentBlockKind.Table).Any(block => block.Text.Contains("Alice")))
        {
            throw new Exception("Table hints must force a table, preserve outside text, and avoid duplicate cells.");
        }
        var noHeader = PdfMarkdownConverter.Convert(doc, new PdfMarkdownOptions
        {
            TableHints = [new PdfTableHint { PageNumber = 1, X = 10, Y = 220, Width = 230, Height = 50, FirstRowIsHeader = false }],
        });
        if (!noHeader.Markdown.Contains("|  |  |\n| --- | --- |\n| Name | City |"))
        {
            throw new Exception("Headerless table hints must retain the first data row.");
        }
        var nameLine = PdfParagraphExtractor.Extract(doc, 1).SelectMany(paragraph => paragraph.Lines).Single(line => line.Text == "Name");
        var edge = nameLine.Baseline + nameLine.Height / 2;
        var adjacent = PdfMarkdownConverter.Convert(doc, new PdfMarkdownOptions
        {
            TableHints =
            [
                new PdfTableHint { PageNumber = 1, X = 10, Y = 220, Width = 230, Height = edge - 220 },
                new PdfTableHint { PageNumber = 1, X = 10, Y = edge, Width = 230, Height = 270 - edge },
            ],
        });
        if (adjacent.Document.Pages[0].Blocks.Count(block => block.Kind == PdfContentBlockKind.Table) != 2
            || adjacent.Markdown.Split("Name").Length != 2)
        {
            throw new Exception("Adjacent hint borders must not duplicate text fragments.");
        }
        foreach (var hints in new IReadOnlyList<PdfTableHint>[]
        {
            [hint, hint],
            [new PdfTableHint { PageNumber = 2, Width = 10, Height = 10 }],
            [new PdfTableHint { PageNumber = 1, Width = -1, Height = 10 }],
            [new PdfTableHint { PageNumber = 1, X = double.NaN, Width = 10, Height = 10 }],
            [new PdfTableHint { PageNumber = 1, X = 500, Y = 500, Width = 10, Height = 10 }],
        })
        {
            try
            {
                PdfMarkdownConverter.Convert(doc, new PdfMarkdownOptions { TableHints = hints });
                throw new Exception("Invalid or empty table hints must be rejected.");
            }
            catch (ArgumentException)
            {
                // Invalid user hints are explicit errors rather than silently ignored.
            }
        }
        if (PdfConverter.ToJson(doc) != before)
        {
            throw new Exception("Table hints must not modify source PDF objects.");
        }
        Console.WriteLine("Table hint checks passed.");
    }
}
