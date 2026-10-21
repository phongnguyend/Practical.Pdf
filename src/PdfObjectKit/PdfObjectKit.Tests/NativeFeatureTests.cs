using System.Text;
using PdfObjectKit;
using static PdfObjectKit.PdfPrimitives;

internal static class NativeFeatureTests
{
    public static void Run(string? fontPath)
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new Exception("Native feature: " + message);
            }

            checks++;
        }

        void Reject(Action action, string message)
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

            throw new Exception("Native feature accepted: " + message);
        }

        PdfObjectDocument Fixture(string content)
        {
            var bytes = PdfFixture.Create(null, new FixturePage(Width: 200, Height: 200));
            var result = PdfReader.Read(bytes, false);
            var stream = result.Objects.Single(o => o.Value.Type == "stream").Value;
            stream.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content));
            return result;
        }

        PdfPageInterpreter Interpret(PdfObjectDocument document)
        {
            var graph = new PdfGraph(document);
            return new(graph, graph.Pages()[0]);
        }

        var graph = Fixture("BT /F1 10 Tf 1 0 0 1 10 100 Tm (A) Tj 0 -20 Td (B) Tj ET");
        var page = Interpret(graph);
        Check(page.Text == "AB" && page.TextWarning is null, "basic drawing interpretation");
        Check(page.Letters[0].StartBaseLine == new PdfImagePoint(10, 100) && page.Letters[1].StartBaseLine == new PdfImagePoint(10, 80), "text matrix positioning");
        var content = graph.Objects.Single(o => o.Value.Type == "stream").Value;
        var raw = Convert.FromBase64String(content.Base64!);
        content.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToHexString(raw) + ">"));
        content.Entries!["Filter"] = Name("ASCIIHexDecode");
        Check(Interpret(graph).Text == "AB", "ASCIIHex content");
        content.Base64 = Convert.ToBase64String([(byte)(raw.Length - 1), ..raw, 128]);
        content.Entries["Filter"] = Name("RunLengthDecode");
        Check(Interpret(graph).Text == "AB", "RunLength content");
        content.Entries["Filter"] = Name("ASCII85Decode");
        // Whitespace-only tuple: 0x20,0x20,0x20,0x20 = +<VdL.
        content.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("+<VdL~>"));
        Check(Interpret(graph).Text == "" && Interpret(graph).TextWarning is null, "ASCII85 content");
        content.Entries["Filter"] = Name("UnsupportedFilter");
        Check(Interpret(graph).TextWarning is not null, "unsupported filter reported");
        graph.TextIndex = [];
        Check(PdfWriter.Write(graph).Length > 0, "unsupported content preserved structurally");
        graph = Fixture("q 2 0 0 3 4 5 cm BT /F1 10 Tf 1 0 0 1 10 20 Tm (X) Tj ET Q");
        page = Interpret(graph);
        Check(page.Letters.Single().StartBaseLine == new PdfImagePoint(24, 65), "graphics and text matrix composition");
        graph = Fixture("BT /F1 10 Tf 10 100 Td 20 TL (a) Tj (b) ' 2 3 (c) \" ET");
        page = Interpret(graph);
        Check(page.Text == "abc" && page.Letters[2].StartBaseLine.Y == 60, "quote operators and leading indexed");
        graph = Fixture("BT /F1 10 Tf 2 Tc (test) Tj ET");
        graph.TextIndex = PdfTextIndexer.Build(graph);
        Reject(() => PdfTextEditor.UpdateText(graph, 1, graph.TextIndex[0].Runs![0], "edit"), "character spacing edit");
        graph = Fixture("BT /F1 10 Tf <414243> Tj ET");
        var font = graph.Objects.Single(o => o.Value.Entries?.GetValueOrDefault("Type")?.Name == "Font").Value;
        var mapId = new PdfReference(graph.Objects.Max(o => o.Id.ObjectNumber) + 1);
        font.Entries!["ToUnicode"] = Ref(mapId);
        graph.Objects.Add(new(mapId, Stream(Encoding.ASCII.GetBytes("1 beginbfrange <41> <43> <0061> endbfrange"))));
        Check(Interpret(graph).Text == "abc", "ToUnicode sequential range");
        graph.Objects[^1].Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("1 beginbfrange <41> <43> [<0078> <0079> <007A>] endbfrange"));
        Check(Interpret(graph).Text == "xyz", "ToUnicode array range");
        font.Entries["Subtype"] = Name("Type0");
        font.Entries["Encoding"] = Name("Identity-V");
        font.Entries["DescendantFonts"] = Array(Dict(("DW", Num(500))));
        Check(Interpret(graph).TextWarning is not null, "vertical/invalid composite font warns");
        foreach (var number in new[]
        {
            1e-14,
            -1e-40,
            1e30,
            double.Epsilon
        }

        )
        {
            var text = Num(number).Number!;
            Check(!text.Contains('E') && double.Parse(text, System.Globalization.CultureInfo.InvariantCulture) == number, "PDF decimal without exponent");
        }

        if (fontPath is not null)
        {
            var bytes = File.ReadAllBytes(fontPath);
            var fontPage = new FixturePage("A\u1ec7", Width: 200, Height: 200, X: 10);
            var pdf = PdfFixture.Create(bytes, fontPage);
            using var independent = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(pdf), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            Check(independent.Pages[0].Resources.Elements.GetDictionary("/Font")!.Elements.Count == 1, "independent reader sees embedded font");
            graph = PdfReader.Read(pdf);
            Check(graph.TextIndex[0].Text == "A\u1ec7", "native Unicode round trip");
            Check(graph.Objects.Any(o => o.Value.Entries?.GetValueOrDefault("FontFile2")is not null), "TrueType descriptor emitted");
            Check(graph.Objects.Where(o => o.Value.Type == "stream").Any(o => Convert.FromBase64String(o.Value.Base64!).SequenceEqual(bytes)), "full original font program embedded");
            Reject(() => PdfTextEditor.UpdateText(graph, 1, graph.TextIndex[0].Runs![0], "X\u1ec7"), "missing original font mapping");
            PdfTextEditor.UpdateText(graph, 1, graph.TextIndex[0].Runs![0], "X\u1ec7", new() { FallbackFont = bytes });
            var edited = PdfWriter.Write(graph);
            Check(PdfReader.Read(edited).TextIndex[0].Text == "X\u1ec7", "fallback font edit");
            Reject(() => PdfFixture.Create([0, 1, 2], fontPage), "truncated font");
        }

        Console.WriteLine($"Passed {checks} native feature checks.");
    }
}
