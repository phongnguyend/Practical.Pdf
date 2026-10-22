using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using PdfObjectKit.Converters;
using System.Text;
using PdfObjectKit;
using PdfObjectKit.Editors;

internal static class TextEditorTests
{
    public static void Run(string? fontPath)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Text editor: " + name);
            }

            checks++;
        }

        var document = PdfReader.Read(PdfFixture.Create(null, new("Original"), new("Other page")));
        void Reject(Action action, string name)
        {
            var before = PdfConverter.ToJson(document);
            try
            {
                action();
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                Check(PdfConverter.ToJson(document) == before, "atomic failure: " + name);
                return;
            }

            throw new Exception("Text editor accepted " + name);
        }

        var run = PdfTextExtractor.Extract(document)[0].Runs!.Single();
        var originalBytes = document.Objects.Single(o => o.Id == run.StreamObject).Value.Base64;
        PdfTextEditor.UpdateText(document, 1, run, "A much longer replacement");
        Check(PdfTextExtractor.Extract(document)[0].Text == "A much longer replacement", "run overload updates text immediately");
        Check(document.Objects.Single(o => o.Id == run.StreamObject).Value.Base64 != originalBytes, "actual object bytes changed");
        Check(PdfTextExtractor.Extract(document)[1].Text == "Other page", "other page preserved");
        Check(PdfTextExtractor.Extract(document)[0].Runs!.Single().FontSize == run.FontSize, "font size preserved");
        Check(PdfTextExtractor.Extract(PdfReader.Read(PdfWriter.Write(document)))[0].Text == "A much longer replacement", "PDF writer round trip");
        Reject(() => PdfTextEditor.UpdateText(document, 1, run, "Stale"), "stale run");
        run = PdfTextExtractor.Extract(document)[0].Runs!.Single();
        PdfTextEditor.UpdateText(document, 1, run.StreamObject, run.OperatorIndex, run.OperandIndex, "Again");
        Check(PdfTextExtractor.Extract(document)[0].Text == "Again", "object/operation overload");
        run = PdfTextExtractor.Extract(document)[0].Runs!.Single();
        Reject(() => PdfTextEditor.UpdateText(document, 0, run, "Invalid"), "page number");
        Reject(() => PdfTextEditor.UpdateText(document, 2, run, "Invalid"), "wrong page");
        Reject(() => PdfTextEditor.UpdateText(document, 1, run.StreamObject, 999, -1, "Invalid"), "operator identity");
        Reject(() => PdfTextEditor.UpdateText(document, 1, run, "\u4e2d"), "missing glyph");
        Reject(() => PdfTextEditor.UpdateText(document, 1, run, "two\nlines"), "newlines");
        Reject(() => PdfTextEditor.UpdateText(document, 1, run, null!), "null replacement");
        PdfTextEditor.UpdateText(document, 1, run, "");
        Check(PdfTextExtractor.Extract(PdfReader.Read(PdfWriter.Write(document)))[0].Text == "", "empty text deletion without stored index");

        document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
        var stream = PdfTextExtractor.Extract(document)[0].ContentStreams.Single();
        document.Objects.Single(o => o.Id == stream).Value.Base64 = Convert.ToBase64String(
            Encoding.ASCII.GetBytes("BT /F1 12 Tf 20 100 Td [(Repeat) 0 (Repeat)] TJ ( tail) Tj ET"));
        document = PdfReader.Read(PdfWriter.Write(document));
        run = PdfTextExtractor.Extract(document)[0].Runs![1];
        var oldGraph = new PdfGraph(document);
        var oldTail = new PdfPageInterpreter(oldGraph, oldGraph.Pages()[0]).Letters.Last().StartBaseLine;
        PdfTextEditor.UpdateText(document, 1, run, "Longer second occurrence");
        Check(PdfTextExtractor.Extract(document)[0].Text == "RepeatLonger second occurrence tail", "only selected duplicate TJ string changes");
        var newGraph = new PdfGraph(document);
        var newTail = new PdfPageInterpreter(newGraph, newGraph.Pages()[0]).Letters.Last().StartBaseLine;
        Check(Math.Abs(newTail.X - oldTail.X) < 0.001, "following text position preserved");

        document = PdfReader.Read(PdfFixture.Create(null, new("Original"), new("Other page")));
        run = PdfTextExtractor.Extract(document)[0].Runs!.Single();
        PdfTextExtractor.Extract(document)[1].Runs![0].Text = "Pending edit";
        PdfTextEditor.UpdateText(document, 1, run, "Direct edit");
        Check(PdfTextExtractor.Extract(document)[1].Text == "Other page", "index edit on another page ignored");
        run = PdfTextExtractor.Extract(document)[0].Runs!.Single();
        document.Objects[0].Value.Entries!["Shared"] = new() { Type = "reference", Reference = run.StreamObject };
        Reject(() => PdfTextEditor.UpdateText(document, 1, run, "Changed"), "shared stream");

        if (fontPath is not null)
        {
            var font = File.ReadAllBytes(fontPath);
            document = PdfReader.Read(PdfFixture.Create(font, new FixturePage("Tiếng Việt")));
            run = PdfTextExtractor.Extract(document)[0].Runs!.Single();
            Reject(() => PdfTextEditor.UpdateText(document, 1, run, "X Việt"), "subset missing X");
            PdfTextEditor.UpdateText(document, 1, run, "X Việt", new() { FallbackFont = font });
            Check(PdfTextExtractor.Extract(document)[0].Text == "X Việt", "fallback font replacement");
        }

        void CheckSpacing(string spacing, string show, string replacement, PdfTextEditOptions? options = null)
        {
            var spaced = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
            var contentId = PdfTextExtractor.Extract(spaced)[0].ContentStreams.Single();
            var content = spaced.Objects.Single(o => o.Id == contentId).Value;
            content.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes($"BT /F1 12 Tf 65 Tz 20 100 Td {spacing} {show} (tail) Tj ET"));
            content.Entries!.Remove("Filter");
            content.Entries.Remove("DecodeParms");
            content.Entries.Remove("Length");
            var beforeGraph = new PdfGraph(spaced);
            var beforeSlots = new PdfPageInterpreter(beforeGraph, beforeGraph.Pages()[0]).Slots;
            var tailPosition = beforeSlots.Last().BaselineStart;
            var selectedRun = PdfTextExtractor.Extract(spaced)[0].Runs!.Single(r => r.Text == "A B");
            PdfTextEditor.UpdateText(spaced, 1, selectedRun, replacement, options);
            spaced = PdfReader.Read(PdfWriter.Write(spaced));
            var afterGraph = new PdfGraph(spaced);
            var afterSlots = new PdfPageInterpreter(afterGraph, afterGraph.Pages()[0]).Slots;
            Check(string.Concat(afterSlots.Select(s => s.Text)) == string.Concat(beforeSlots.Select(s => s.Text == "A B" ? replacement : s.Text)), "spacing edit text round trip");
            Check(Math.Abs(afterSlots.Last().BaselineStart.X - tailPosition.X) < .001
                && Math.Abs(afterSlots.Last().BaselineStart.Y - tailPosition.Y) < .001, "spacing edit preserves following text position");
            Check(afterSlots.Last().CharacterSpacing == beforeSlots.Last().CharacterSpacing
                && afterSlots.Last().WordSpacing == beforeSlots.Last().WordSpacing, "spacing state preserved");
        }

        foreach (var spacing in new[] { "2 Tc 5 Tw", "-1 Tc -3 Tw", "2 Tc 5 Tw q 8 Tc 9 Tw (prefix) Tj Q", "2 Tc 5 Tw 0 Tc 0 Tw" })
        {
            foreach (var replacement in new[] { "Longer A B text", "A", "" })
            {
                CheckSpacing(spacing, "(A B) Tj", replacement);
                CheckSpacing(spacing, "[(before) -120 (A B) 50 (after)] TJ", replacement);
            }
        }

        if (fontPath is not null)
        {
            CheckSpacing("2 Tc 5 Tw", "(A B) Tj", "\u03a9 x", new() { FallbackFont = File.ReadAllBytes(fontPath) });
        }

        Console.WriteLine($"Passed {checks} direct text editor checks.");
    }
}
