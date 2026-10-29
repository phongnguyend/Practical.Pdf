using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class TextSearchTests
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Search: " + message);
            }
        }
        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Placeholder"), new FixturePage("staff Code staffing"), new FixturePage()));
        var page = new PdfGraph(document).Pages()[0];
        const string content = "BT /F1 12 Tf 1 0 0 1 20 700 Tm (Sta) Tj [(ff) -280 (Code)] TJ ET "
            + "BT /F1 12 Tf 1 0 0 1 20 680 Tm (Next line) Tj ET "
            + "BT /F1 12 Tf 0 1 -1 0 300 200 Tm (Rotated) Tj ET "
            + "BT /F1 12 Tf 1 0 0 1 20 600 Tm (Left) Tj 1 0 0 1 400 600 Tm (Right) Tj ET";
        page.Streams[0].Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content));
        var formId = new PdfReference(document.Objects.Max(item => item.Id.ObjectNumber) + 1);
        var form = Stream(Encoding.ASCII.GetBytes("BT /F1 12 Tf 10 20 Td (Form word) Tj ET"));
        form.Entries!["Type"] = Name("XObject");
        form.Entries["Subtype"] = Name("Form");
        form.Entries["BBox"] = Array(Num(0), Num(0), Num(200), Num(100));
        form.Entries["Resources"] = Dict(("Font", page.Resources.Entries!["Font"]));
        document.Objects.Add(new(formId, form));
        page.Resources.Entries["XObject"] = Dict(("Shared", Ref(formId)));
        page.Streams[0].Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content + " q 1 0 0 1 0 400 cm /Shared Do Q q 1 0 0 1 0 300 cm /Shared Do Q"));
        var before = PdfConverter.ToJson(document);
        var index = PdfTextSearcher.CreateIndex(document);
        var matches = index.Search("staff code").Matches;
        Check(matches.Count == 2 && matches[0].PageNumber == 1 && matches[1].PageNumber == 2, "matches across runs, TJ spacing and pages");
        Check(matches[0].Regions.Count == 9 && Math.Abs(matches[0].Regions[0].Corners[0].X - 20) < .01, "partial glyph highlights start at source position");
        Check(index.Search("Staff Code", new() { MatchCase = true }).Matches.Count == 1, "case sensitive");
        Check(index.Search("staff", new() { WholeWord = true }).Matches.Count == 2, "whole word excludes staffing");
        Check(index.Search("staff").Matches.Count == 3, "substring search");
        Check(index.Search("Code   Next\nline").Matches.Count == 1, "phrase across lines with whitespace normalization");
        Check(index.Search("Left Right").Matches.Count == 0, "no phrase across distant columns");
        Check(index.Search("Form word").Matches.Count == 2, "repeated Form occurrences");
        var rotated = index.Search("Rotated").Matches.Single();
        Check(Math.Abs(rotated.Regions[0].Corners[1].X - rotated.Regions[0].Corners[0].X) < .01 && rotated.Regions[0].Corners[1].Y > rotated.Regions[0].Corners[0].Y, "rotated quad follows text matrix");
        var capped = index.Search("staff", new() { MaxResults = 1 });
        Check(capped.Truncated && capped.Matches.Count == 1, "result cap");
        Check(!index.Search("Rotated", new() { MaxResults = 1 }).Truncated, "exact cap is not truncated");
        Check(index.Search("absent").Matches.Count == 0 && index.Search("  ").Matches.Count == 0, "empty/no match");
        Check(index.Search("staff").Warnings.Any(warning => warning.Contains("Page 3")), "image-only/blank page note");
        Check(before == PdfConverter.ToJson(document), "search does not mutate source");
        PdfWatermarkEditor.AddText(document, new() { Text = "WATERMARK" });
        Check(PdfTextSearcher.Search(document, "WATERMARK").Matches.Count == 0, "managed decorative watermark excluded");
        Check(index.Search("staff code").Matches.Count == 2, "index remains reusable after source mutation");
        var normalized = PdfTextSearchIndex.Normalize("o\ufb03ce cafe\u0301", Enumerable.Range(0, 11).ToList());
        Check(normalized.Text == "office caf\u00e9" && normalized.Mapping[1].SequenceEqual(normalized.Mapping[3]), "ligature expansion maps to same glyph");
        Check(normalized.Mapping[^1].Length == 2, "combining characters retain both source glyphs");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try
        {
            index.Search("staff", cancellationToken: cancel.Token);
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException)
        {
            // Repeated search is cancellable too.
        }
        Console.WriteLine("Text search runs, pages, Unicode, geometry, Forms, options and limits passed.");
    }
}
