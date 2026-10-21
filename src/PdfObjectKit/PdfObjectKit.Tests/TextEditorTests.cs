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

        var run = document.TextIndex[0].Runs!.Single();
        var originalBytes = document.Objects.Single(o => o.Id == run.StreamObject).Value.Base64;
        PdfTextEditor.UpdateText(document, 1, run, "A much longer replacement");
        Check(document.TextIndex[0].Text == "A much longer replacement", "run overload updates text immediately");
        Check(document.Objects.Single(o => o.Id == run.StreamObject).Value.Base64 != originalBytes, "actual object bytes changed");
        Check(document.TextIndex[1].Text == "Other page", "other page preserved");
        Check(document.TextIndex[0].Runs!.Single().FontSize == run.FontSize, "font size preserved");
        Check(PdfReader.Read(PdfWriter.Write(document)).TextIndex[0].Text == "A much longer replacement", "PDF writer round trip");
        Reject(() => PdfTextEditor.UpdateText(document, 1, run, "Stale"), "stale run");
        run = document.TextIndex[0].Runs!.Single();
        PdfTextEditor.UpdateText(document, 1, run.StreamObject, run.OperatorIndex, run.OperandIndex, "Again");
        Check(document.TextIndex[0].Text == "Again", "object/operation overload");
        run = document.TextIndex[0].Runs!.Single();
        Reject(() => PdfTextEditor.UpdateText(document, 0, run, "Invalid"), "page number");
        Reject(() => PdfTextEditor.UpdateText(document, 2, run, "Invalid"), "wrong page");
        Reject(() => PdfTextEditor.UpdateText(document, 1, run.StreamObject, 999, -1, "Invalid"), "operator identity");
        Reject(() => PdfTextEditor.UpdateText(document, 1, run, "\u4e2d"), "missing glyph");
        Reject(() => PdfTextEditor.UpdateText(document, 1, run, "two\nlines"), "newlines");
        Reject(() => PdfTextEditor.UpdateText(document, 1, run, null!), "null replacement");
        document.TextIndex.Clear();
        PdfTextEditor.UpdateText(document, 1, run, "");
        Check(PdfReader.Read(PdfWriter.Write(document)).TextIndex[0].Text == "", "empty text deletion without stored index");

        document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
        var stream = document.TextIndex[0].ContentStreams.Single();
        document.Objects.Single(o => o.Id == stream).Value.Base64 = Convert.ToBase64String(
            Encoding.ASCII.GetBytes("BT /F1 12 Tf 20 100 Td [(Repeat) 0 (Repeat)] TJ ( tail) Tj ET"));
        document.TextIndex.Clear();
        document = PdfReader.Read(PdfWriter.Write(document));
        run = document.TextIndex[0].Runs![1];
        var oldGraph = new PdfGraph(document);
        var oldTail = new PdfPageInterpreter(oldGraph, oldGraph.Pages()[0]).Letters.Last().StartBaseLine;
        PdfTextEditor.UpdateText(document, 1, run, "Longer second occurrence");
        Check(document.TextIndex[0].Text == "RepeatLonger second occurrence tail", "only selected duplicate TJ string changes");
        var newGraph = new PdfGraph(document);
        var newTail = new PdfPageInterpreter(newGraph, newGraph.Pages()[0]).Letters.Last().StartBaseLine;
        Check(Math.Abs(newTail.X - oldTail.X) < 0.001, "following text position preserved");

        document = PdfReader.Read(PdfFixture.Create(null, new("Original"), new("Other page")));
        run = document.TextIndex[0].Runs!.Single();
        document.TextIndex[1].Runs![0].Text = "Pending edit";
        PdfTextEditor.UpdateText(document, 1, run, "Direct edit");
        Check(document.TextIndex[1].Text == "Other page", "index edit on another page ignored");
        run = document.TextIndex[0].Runs!.Single();
        document.Objects[0].Value.Entries!["Shared"] = new() { Type = "reference", Reference = run.StreamObject };
        Reject(() => PdfTextEditor.UpdateText(document, 1, run, "Changed"), "shared stream");

        if (fontPath is not null)
        {
            var font = File.ReadAllBytes(fontPath);
            document = PdfReader.Read(PdfFixture.Create(font, new FixturePage("Tiếng Việt")));
            run = document.TextIndex[0].Runs!.Single();
            Reject(() => PdfTextEditor.UpdateText(document, 1, run, "X Việt"), "subset missing X");
            PdfTextEditor.UpdateText(document, 1, run, "X Việt", new() { FallbackFont = font });
            Check(document.TextIndex[0].Text == "X Việt", "fallback font replacement");
        }

        Console.WriteLine($"Passed {checks} direct text editor checks.");
    }
}
