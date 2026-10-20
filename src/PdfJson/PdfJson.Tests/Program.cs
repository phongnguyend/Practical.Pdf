using Practical.PdfJson;

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

var source = new DocumentModel
{
    Pages = [new()
    {
        Width = 600,
        Height = 800,
        Text = [new()
        {
            Value = "Hello world",
            X = 40,
            Y = 740,
            FontSize = 16
        }

        ]
    }, new()
    {
        Width = 800,
        Height = 600,
        Text = [new()
        {
            Value = "Second page",
            X = 50,
            Y = 500
        }

        ]
    }, new()
    {
        Width = 300,
        Height = 300
    }

    ]
};
var pdf = PdfJsonConverter.WritePdf(source);
var extracted = PdfJsonConverter.ReadPdf(pdf, includeOriginal: true);
var parsed = PdfJsonConverter.FromJson(PdfJsonConverter.ToJson(extracted));
Check(parsed.Pages.Count == 3, "page count including blank page");
Check(parsed.Pages[1].Width == 800 && parsed.Pages[1].Height == 600, "landscape dimensions");
Check(string.Join(" ", parsed.Pages[0].Text.Select(t => t.Value)) == "Hello world", "text extraction: " + string.Join("|", parsed.Pages[0].Text.Select(t => t.Value)));
Check(Math.Abs(parsed.Pages[0].Text[0].X - 40) < 0.1 && Math.Abs(parsed.Pages[0].Text[0].Y - 740) < 0.1, "baseline position");
Check(pdf.SequenceEqual(PdfJsonConverter.RestoreOriginal(parsed)), "exact byte restoration");
parsed.Pages[0].Text[0].Value = "Edited";
var rebuilt = PdfJsonConverter.ReadPdf(PdfJsonConverter.WritePdf(parsed));
Check(rebuilt.Pages[0].Text.Any(t => t.Value == "Edited"), "JSON edits applied when rebuilding");
Check(pdf.SequenceEqual(PdfJsonConverter.RestoreOriginal(parsed)), "restore ignores edits explicitly");
Reject(() => PdfJsonConverter.FromJson("null"), "null JSON");
Reject(() => PdfJsonConverter.FromJson("{\"schemaVersion\":2}"), "unknown schema");
Reject(() => PdfJsonConverter.FromJson("{\"pages\":null}"), "null pages");
Reject(() => PdfJsonConverter.FromJson("{\"pages\":[{\"width\":-1}]}"), "invalid dimensions");
Reject(() => PdfJsonConverter.RestoreOriginal(source), "missing embedded original");
source.Pages[0].Text[0].Value = "Tiếng Việt";
Reject(() => PdfJsonConverter.WritePdf(source), "Unicode requires font");
if (args.Length == 1)
{
    var unicodePdf = PdfJsonConverter.WritePdf(source, File.ReadAllBytes(args[0]));
    var unicode = PdfJsonConverter.ReadPdf(unicodePdf);
    Check(string.Join(" ", unicode.Pages[0].Text.Select(t => t.Value)) == "Tiếng Việt", "Unicode with TrueType font");
    var fontGraph = PdfObjectConverter.ReadPdf(unicodePdf);
    var rebuiltFont = PdfObjectConverter.WritePdf(PdfObjectConverter.FromJson(PdfObjectConverter.ToJson(fontGraph)));
    var firstIndexedText = fontGraph.TextIndex[0].Text;
    fontGraph.TextIndex[0].Text = "X" + firstIndexedText[1..];
    var fallbackResult = PdfObjectConverter.WritePdf(fontGraph, new() { FallbackFont = File.ReadAllBytes(args[0]) });
    Check(PdfObjectConverter.ReadPdf(fallbackResult).TextIndex[0].Text == fontGraph.TextIndex[0].Text, "fallback embeds missing glyphs");
    Check(PdfObjectConverter.ReadPdf(fallbackResult).TextIndex[1].Text == fontGraph.TextIndex[1].Text, "fallback leaves other pages unchanged");
    fontGraph.TextIndex[0].Text = firstIndexedText;
    Check(string.Join(" ", PdfJsonConverter.ReadPdf(rebuiltFont).Pages[0].Text.Select(t => t.Value)) == "Tiếng Việt", "embedded font and CMap object round trip");
}

Console.WriteLine($"Passed {checks} integration checks.");
ObjectGraphTests.Run(pdf);
NativeParserTests.Run();
NativeFeatureTests.Run(args.Length == 1 ? args[0] : null);
