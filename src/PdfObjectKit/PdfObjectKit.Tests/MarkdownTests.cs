using PdfObjectKit.Diagnostics;
using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Extractors;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;

internal static class MarkdownTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Markdown: " + name);
            }

            checks++;
        }

        PdfObjectDocument Fixture(string content)
        {
            var doc = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
            var stream = PdfTextExtractor.Extract(doc)[0].ContentStreams.Single();
            doc.Objects.Single(o => o.Id == stream).Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content));
            return doc;
        }

        string Line(int size, int x, int y, string text) => $"BT /F1 {size} Tf 1 0 0 1 {x} {y} Tm ({text}) Tj ET\n";
        var source = Fixture(Line(24, 20, 750, "A long heading") + Line(24, 20, 720, "wrapped across lines")
            + Line(12, 20, 670, "A paragraph with enough text to establish the normal body size.")
            + Line(12, 20, 655, "It continues here.") + Line(18, 20, 610, "Subheading")
            + Line(12, 20, 570, "- First item") + Line(12, 32, 555, "continued here")
            + Line(12, 20, 540, "- Second item") + Line(12, 20, 500, "3. Ordered item"));
        var before = PdfConverter.ToJson(source);
        var result = PdfMarkdownConverter.Convert(source);
        Check(result.Markdown.StartsWith("# A long heading wrapped across lines\n\n"), "multiline heading");
        Check(result.Markdown.Contains("## Subheading"), "document-wide heading levels");
        Check(result.Markdown.Contains("size. It continues here."), "wrapped body lines joined");
        Check(result.Markdown.Contains("- First item continued here\n- Second item"), "list items and hanging continuation");
        Check(result.Markdown.Contains("3. Ordered item"), "ordered list start preserved");
        Check(result.Document.Pages[0].Blocks[0].SourceLines.Count == 2, "source lines retained");
        Check(result.Document.Pages[0].Blocks[0].SourceLines[0].Runs.Count == 1, "source operands retained");
        Check(result.Warnings.Count == 0, "supported document has no omission warnings");
        Check(PdfConverter.ToJson(source) == before, "source unchanged");
        Check(result.Markdown == PdfMarkdownConverter.Render(result.Document), "independent renderer");

        source = Fixture(Line(20, 70, 700, "A wide centered heading") + Line(20, 134, 675, "short")
            + Line(20, 100, 650, "middle line") + Line(12, 20, 600, "Body text."));
        result = PdfMarkdownConverter.Convert(source, new() { BodyTextHeight = 12 });
        // Construct exactly centered lines from measured widths so the test does not assume font metrics.
        var lines = result.Document.Pages[0].Blocks.SelectMany(b => b.SourceLines).Take(3).ToList();
        var centered = "";
        for (var i = 0; i < lines.Count; i++)
        {
            var x = 200 - (lines[i].Right - lines[i].Left) / 2;
            centered += FormattableString.Invariant($"BT /F1 20 Tf 1 0 0 1 {x} {700 - i * 25} Tm ({lines[i].Text}) Tj ET\n");
        }

        result = PdfMarkdownConverter.Convert(Fixture(centered + Line(12, 20, 600, "Body text.")), new() { BodyTextHeight = 12 });
        Check(result.Document.Pages[0].Blocks[0].SourceLines.Count == 3, "three centered heading lines grouped");
        source = Fixture(Line(12, 20, 700, "A hyphen-") + Line(12, 20, 685, "ated word"));
        Check(PdfMarkdownConverter.Convert(source).Markdown.Contains("hyphen- ated"), "hyphens preserved by default");
        Check(PdfMarkdownConverter.Convert(source, new() { JoinHyphenatedWords = true }).Markdown.Contains("hyphenated"), "opt-in dehyphenation");
        result = PdfMarkdownConverter.Convert(Fixture(Line(12, 20, 700, "*literal* [link] <script> & #")));
        Check(result.Markdown.Contains("\\*literal\\* \\[link\\] &lt;script&gt; &amp; \\#"), "Markdown and HTML escaped");
        result = PdfMarkdownConverter.Convert(Fixture(Line(12, 20, 700, "Left") + Line(12, 300, 700, "Right")));
        Check(result.Markdown == "Left\n\nRight\n" && result.Diagnostics.Any(d => d.Code == PdfDiagnosticCode.AmbiguousReadingOrder), "parallel fragments retained with warning");
        result = PdfMarkdownConverter.Convert(Fixture(Line(12, 20, 740, "Intro")
            + Line(12, 20, 700, "Left first") + Line(12, 300, 700, "Right first")
            + Line(12, 20, 685, "Left second") + Line(12, 300, 685, "Right second")
            + Line(12, 20, 640, "Closing")));
        Check(result.Markdown == "Intro\n\nLeft first Left second\n\nRight first Right second\n\nClosing\n", "separated regions preserve column reading order");
        Check(result.Document.Pages[0].Blocks.Count == 4 && result.Document.Pages[0].Blocks[1].SourceLines.Count == 2
            && result.Document.Pages[0].Blocks[2].SourceLines.Count == 2, "lines join within columns only");
        Check(result.Warnings.Count == 0, "resolved columns do not warn");
        result = PdfMarkdownConverter.Convert(Fixture("BT /F1 12 Tf 0 1 -1 0 20 700 Tm (Rotated) Tj ET"));
        Check(result.Markdown == "" && result.Warnings.Count > 0, "rotated text warning");
        Check(PdfMarkdownConverter.Convert(Fixture("")).Markdown == "", "blank page");
        var twoPages = PdfReader.Read(PdfFixture.Create(null, new FixturePage("First"), new FixturePage("Second")));
        result = PdfMarkdownConverter.Convert(twoPages);
        Check(result.Markdown == "First\n\nSecond\n" && result.Document.Pages.Count == 2, "page boundaries retained");
        try
        {
            PdfMarkdownConverter.Convert(twoPages, new() { BodyTextHeight = double.NaN });
            throw new Exception("Invalid height accepted");
        }
        catch (ArgumentOutOfRangeException)
        {
            checks++;
        }

        result = PdfMarkdownConverter.Convert(Fixture("q 10 0 0 10 20 20 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID abc EI Q"));
        Check(result.Diagnostics.Any(d => d.Code == PdfDiagnosticCode.ImagesOmitted) && result.Diagnostics.Any(d => d.Code == PdfDiagnosticCode.OcrRequired), "image-only page reports omissions and missing OCR");
        result = PdfMarkdownConverter.Convert(Fixture(Line(12, 20, 700, "? A question")));
        Check(result.Document.Pages[0].Blocks[0].Kind == PdfContentBlockKind.Paragraph, "question mark is not a list marker");
        result = PdfMarkdownConverter.Convert(Fixture("BT /F2 12 Tf (Missing font) Tj ET"));
        Check(result.Markdown == "" && result.Diagnostics.Any(d => d.Code == PdfDiagnosticCode.TextExtractionFailed), "unsupported extraction warned");
        source = Fixture(Line(20, 20, 700, "Long enough text to be a heading on one page"));
        var corrected = PdfMarkdownConverter.Convert(source).Document;
        corrected.Pages[0].Blocks[0].Kind = PdfContentBlockKind.Heading;
        corrected.Pages[0].Blocks[0].HeadingLevel = 2;
        Check(PdfMarkdownConverter.Render(corrected).StartsWith("## "), "caller can correct classification before rendering");
        result = PdfMarkdownConverter.Convert(Fixture(Line(12, 20, 700, "Full Name:") + Line(12, 240, 700, "John Smith")
            + Line(12, 20, 680, "Phone:") + Line(12, 240, 680, "555-1234")));
        Check(result.Markdown == "Full Name: John Smith\n\nPhone: 555-1234\n", "same-row field values joined without merging adjacent fields");
        Check(result.Document.Pages[0].Blocks[0].SourceLines[0].Runs.Count == 2, "joined field retains source runs");
        result = PdfMarkdownConverter.Convert(Fixture(Line(12, 20, 700, "Call 555-") + Line(12, 20, 685, "0123 or www.example.com.")));
        Check(result.Markdown == "Call 555-0123 or www.example.com.\n", "phone continuation and ordinary punctuation preserved");
        var raised = "BT /F1 12 Tf 1 0 0 1 20 700 Tm (456) Tj /F1 8 Tf 4 Ts (th) Tj /F1 12 Tf 0 Ts ( St) Tj ET";
        Check(PdfMarkdownConverter.Convert(Fixture(raised)).Markdown.Trim() == "456th St", "superscript is kept inline");
        result = PdfMarkdownConverter.Convert(Fixture(Line(12, 20, 700, "Left one") + Line(12, 250, 700, "Middle one")
            + Line(12, 470, 700, "Right one") + Line(12, 20, 685, "Left two")
            + Line(12, 250, 685, "Middle two") + Line(12, 470, 685, "Right two")));
        Check(result.Markdown == "Left one Left two\n\nMiddle one Middle two\n\nRight one Right two\n", "three separated columns ordered left to right");
        Check(result.Warnings.Count == 0 && result.Document.Pages[0].Blocks.SelectMany(b => b.SourceLines).Count() == 6, "column ordering preserves every source line");
        result = PdfMarkdownConverter.Convert(Fixture(Line(12, 20, 700, "Uncertain left") + Line(12, 300, 700, "Uncertain right")));
        Check(result.Warnings.Single().Contains("x=20") && result.Warnings[0].Contains("y=700")
            && result.Warnings[0].Contains("Uncertain left"), "unresolved warning identifies its region");
        result = PdfMarkdownConverter.Convert(Fixture(Line(12, 20, 740, "Left upper") + Line(12, 20, 725, "Left lower")
            + Line(12, 300, 640, "Right upper") + Line(12, 300, 625, "Right lower")));
        Check(result.Markdown == "Left upper Left lower\n\nRight upper Right lower\n", "nonoverlapping vertical regions remain separate");
        Console.WriteLine($"Passed {checks} Markdown checks.");
    }
}
