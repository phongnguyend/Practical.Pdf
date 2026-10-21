using PdfObjectKit;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception($"FAILED: {name}");
    }

    checks++;
}

void Reject(Action action, string name)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        checks++;
        return;
    }

    throw new Exception($"FAILED: {name}");
}

var source = new FixturePage[]
{
    new("Hello world", Width: 600, Height: 800, X: 40, Y: 740, FontSize: 16),
    new("Second page", Width: 800, Height: 600, X: 50, Y: 500),
    new(Width: 300, Height: 300)
};
var pdf = PdfFixture.Create(null, source);
var parsed = PdfConverter.FromJson(PdfConverter.ToJson(PdfReader.Read(pdf)));
var graph = new PdfGraph(parsed);
var pages = graph.Pages();
Check(pages.Count == 3, "page count including blank page");
Check(pages[1].Width == 800 && pages[1].Height == 600, "landscape dimensions");
Check(parsed.TextIndex[0].Text == "Hello world", "text extraction");
var first = new PdfPageInterpreter(graph, pages[0]).Letters[0];
Check(Math.Abs(first.StartBaseLine.X - 40) < 0.1 && Math.Abs(first.StartBaseLine.Y - 740) < 0.1, "baseline position");
parsed.TextIndex[0].Runs![0].Text = "Edited";
Check(PdfReader.Read(PdfWriter.Write(parsed)).TextIndex[0].Text == "Hello world", "JSON text edits ignored when rebuilding");
Reject(() => PdfConverter.FromJson("null"), "null JSON");
Reject(() => PdfConverter.FromJson("{\"schemaVersion\":2,\"trailer\":{\"type\":\"null\"}}"), "unknown schema");
if (args.Length == 1)
{
    source[0] = source[0] with { Text = "Ti?ng Vi?t" };
    var unicodePdf = PdfFixture.Create(File.ReadAllBytes(args[0]), source);
    var fontGraph = PdfReader.Read(unicodePdf);
    Check(fontGraph.TextIndex[0].Text == "Ti?ng Vi?t", "Unicode with TrueType font");
    var rebuiltFont = PdfWriter.Write(PdfConverter.FromJson(PdfConverter.ToJson(fontGraph)));
    var firstIndexedText = fontGraph.TextIndex[0].Text;
    PdfTextEditor.UpdateText(fontGraph, 1, fontGraph.TextIndex[0].Runs![0], "X" + firstIndexedText[1..], new() { FallbackFont = File.ReadAllBytes(args[0]) });
    var fallbackResult = PdfWriter.Write(fontGraph);
    Check(PdfReader.Read(fallbackResult).TextIndex[0].Text == fontGraph.TextIndex[0].Text, "fallback embeds missing glyphs");
    Check(PdfReader.Read(fallbackResult).TextIndex[1].Text == fontGraph.TextIndex[1].Text, "fallback leaves other pages unchanged");
    Check(PdfReader.Read(rebuiltFont).TextIndex[0].Text == "Ti?ng Vi?t", "embedded font and CMap object round trip");
}

Console.WriteLine($"Passed {checks} integration checks.");
ObjectGraphTests.Run(pdf);
NativeParserTests.Run();
NativeFeatureTests.Run(args.Length == 1 ? args[0] : null);
ImageInsertionTests.Run();
TextEditorTests.Run(args.Length == 1 ? args[0] : null);
