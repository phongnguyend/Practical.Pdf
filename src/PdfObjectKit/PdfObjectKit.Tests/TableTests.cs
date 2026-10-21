using PdfObjectKit.Analysis;
using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Extractors;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;

internal static class TableTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Table: " + name);
            }

            checks++;
        }

        PdfObjectDocument Fixture(string content)
        {
            var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
            var id = PdfTextExtractor.Extract(document)[0].ContentStreams.Single();
            document.Objects.Single(o => o.Id == id).Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content));
            return document;
        }

        string Line(int x, int y, string text) => $"BT /F1 12 Tf 1 0 0 1 {x} {y} Tm ({text}) Tj ET\n";
        string Row(int y, string description, string quantity, string amount) => Line(20, y, description)
            + (quantity == "" ? "" : Line(240, y, quantity)) + Line(360, y, amount);
        var content = Line(20, 750, "Invoice") + Row(700, "Description", "Quantity", "Amount")
            + Row(680, "Service A", "2", "100.00") + Line(26, 665, "extra details")
            + Row(645, "Service B", "", "50.00") + Row(625, "Service C", "1", "25.00")
            + Line(20, 610, "Thanks for your business");
        var document = Fixture(content);
        var before = PdfConverter.ToJson(document);
        var result = PdfMarkdownConverter.Convert(document);
        var tables = result.Document.Pages[0].Blocks.Where(b => b.Kind == PdfContentBlockKind.Table).ToList();
        Check(tables.Count == 1, "one table detected");
        var table = tables[0].Table!;
        Check(table.Rows.Count == 4 && table.Rows.All(r => r.Cells.Count == 3), "rows and columns");
        Check(table.Rows[0].IsHeader, "numeric transition identifies header");
        Check(table.Rows[1].Cells[0].Text == "Service A\nextra details", "wrapped cell joined");
        Check(table.Rows[2].Cells[1].Text == "", "missing middle cell retained");
        Check(table.Rows[1].Cells[0].SourceLines.Count == 2, "wrapped source lines retained");
        Check(table.Rows[1].Cells[0].SourceLines[0].Runs.Count == 1, "source object mapping retained");
        Check(result.Markdown.Contains("| --- | ---: | ---: |"), "numeric columns right aligned");
        Check(result.Markdown.Contains("Service A<br>extra details"), "cell line break rendered");
        Check(result.Document.Pages[0].Blocks.Count == 3 && result.Document.Pages[0].Blocks[^1].Text == "Thanks for your business", "surrounding prose preserved");
        Check(result.Document.Pages[0].Blocks.SelectMany(b => b.SourceLines).Distinct().Count()
            == result.Document.Pages[0].Blocks.Sum(b => b.SourceLines.Count), "table content not duplicated as paragraphs");
        Check(result.Warnings.Count == 0, "recognized table does not emit ambiguous-layout warning");
        Check(before == PdfConverter.ToJson(document), "source graph unchanged");

        result = PdfMarkdownConverter.Convert(Fixture(Row(700, "A", "1", "20") + Row(680, "B", "2", "30") + Row(660, "C", "3", "40")));
        Check(result.Markdown == "|  |  |  |\n| --- | ---: | ---: |\n| A | 1 | 20 |\n| B | 2 | 30 |\n| C | 3 | 40 |\n", "headerless table uses empty Markdown header and retains every data row");
        result = PdfMarkdownConverter.Convert(Fixture(Row(700, "Left text", "Center text", "Right text")
            + Row(680, "More prose", "More prose", "More prose") + Row(660, "Last line", "Last line", "Last line")));
        Check(result.Document.Pages[0].Blocks.All(b => b.Kind != PdfContentBlockKind.Table) && result.Markdown.Contains("More prose"), "prose columns remain text");
        result = PdfMarkdownConverter.Convert(Fixture(Row(700, "A", "1", "20") + Row(680, "B", "2", "30")));
        Check(result.Document.Pages[0].Blocks.All(b => b.Kind != PdfContentBlockKind.Table), "two rows insufficient evidence");

        string RightAligned(int right, int y, string text)
        {
            var measured = PdfParagraphExtractor.Extract(Fixture(Line(20, 700, text)), 1)[0].Lines[0];
            var left = right - (measured.Right - measured.Left);
            return FormattableString.Invariant($"BT /F1 12 Tf 1 0 0 1 {left} {y} Tm ({text}) Tj ET\n");
        }

        result = PdfMarkdownConverter.Convert(Fixture(Line(20, 700, "Item") + RightAligned(400, 700, "Amount")
            + Line(20, 680, "A") + RightAligned(400, 680, "1")
            + Line(20, 660, "B") + RightAligned(400, 660, "1234.56")));
        Check(result.Document.Pages[0].Blocks.Single().Kind == PdfContentBlockKind.Table, "numeric column with varying left edges detected by right alignment");

        var sparse = Line(20, 700, "Date") + Line(120, 700, "Description") + Line(300, 700, "Debit")
            + Line(400, 700, "Credit") + Line(500, 700, "Balance")
            + Line(20, 667, "01/01") + Line(120, 667, "Opening") + Line(500, 667, "$100")
            + Line(20, 634, "01/02") + Line(120, 634, "Deposit") + Line(400, 634, "$50") + Line(500, 634, "$150")
            + Line(20, 601, "01/03") + Line(120, 601, "Payment") + Line(300, 601, "$10") + Line(500, 601, "$140");
        result = PdfMarkdownConverter.Convert(Fixture(sparse));
        var sparseTable = result.Document.Pages[0].Blocks.Single().Table!;
        Check(sparseTable.Rows.Count == 4 && sparseTable.Rows.All(r => r.Cells.Count == 5), "sparse table needs no fully populated data row");
        Check(sparseTable.Rows[1].Cells[2].Text == "" && sparseTable.Rows[1].Cells[3].Text == ""
            && sparseTable.Rows[2].Cells[3].Text == "$50" && sparseTable.Rows[3].Cells[2].Text == "$10", "sparse numeric values retain exact columns");
        result = PdfMarkdownConverter.Convert(Fixture(Line(20, 700, "Opening") + Line(300, 702, "$100")
            + Line(20, 667, "Deposit") + Line(300, 669, "$") + Line(320, 669, "50")
            + Line(20, 634, "Closing") + Line(300, 636, "$150")));
        Check(result.Document.Pages[0].Blocks.Single().Table!.Rows[1].Cells[1].Text == "$50", "currency fragments and offset baselines normalize before table detection");

        var singleRows = PdfParagraphExtractor.Extract(Fixture(Line(20, 700, "Salesperson") + Line(180, 700, "PO Number")
            + Line(340, 700, "Terms") + Line(180, 680, "PO-3333")), 1).SelectMany(p => p.Lines).ToList();
        foreach (var line in singleRows.Where(l => l.Baseline == 700))
        {
            foreach (var run in line.Runs)
            {
                run.FontName = "Helvetica-Bold";
            }
        }

        var singleTable = PdfTableDetector.Detect(singleRows).Single().Table!;
        Check(singleTable.Rows.Count == 2 && singleTable.Rows[0].IsHeader, "styled header supports one data row");
        Check(singleTable.Rows[1].Cells[0].Text == "" && singleTable.Rows[1].Cells[1].Text == "PO-3333"
            && singleTable.Rows[1].Cells[2].Text == "", "single populated cell keeps empty neighboring columns");
        Check(singleTable.Rows[1].Cells[1].SourceLines[0].Runs.Count > 0, "single-row source references retained");
        Check(PdfTableDetector.Detect(singleRows.Where(l => l.Baseline == 700).ToList()).Count == 0, "header alone does not imply a table");
        foreach (var line in singleRows)
        {
            foreach (var run in line.Runs)
            {
                run.FontName = "Helvetica";
            }
        }

        Check(PdfTableDetector.Detect(singleRows).Count == 0, "ambiguous single row without header evidence stays text");

        var manual = new PdfStructuredDocument
        {
            Pages = [new PdfContentPage { PageNumber = 1, Blocks = [new PdfContentBlock
            {
                Kind = PdfContentBlockKind.Table,
                Table = new PdfTable { Rows = [
                    new PdfTableRow { IsHeader = true, Cells = [new() { Text = "Name" }, new() { Text = "Value" }] },
                    new PdfTableRow { Cells = [new() { Text = "a|b\n<script>" }, new() { Text = "5" }] }
                ] }
            }] }]
        };
        var markdown = PdfMarkdownConverter.Render(manual);
        Check(markdown.Contains(@"a\|b<br>&lt;script&gt;"), "cell pipe and HTML escaped");
        manual.Pages[0].Blocks[0].Table!.Rows[0].IsHeader = false;
        markdown = PdfMarkdownConverter.Render(manual);
        Check(markdown.Contains("| --- | --- |"), "headerless alignment includes first data row");
        Check(markdown.Contains("| Name | Value |"), "first data row not promoted to header");
        manual.Pages[0].Blocks[0].Table!.Rows[0].IsHeader = true;
        manual.Pages[0].Blocks[0].Table!.Rows[0].Cells[0].ColumnSpan = 2;
        manual.Pages[0].Blocks[0].Table!.Rows[0].Cells.RemoveAt(1);
        markdown = PdfMarkdownConverter.Render(manual);
        Check(markdown.Contains("colspan=\"2\"") && markdown.Contains("&lt;script&gt;"), "merged-cell HTML fallback");
        manual.Pages[0].Blocks[0].Table!.Rows[0].Cells[0].RowSpan = 0;
        try
        {
            PdfMarkdownConverter.Render(manual);
            throw new Exception("Invalid span accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }

        Console.WriteLine($"Passed {checks} table checks.");
    }
}
