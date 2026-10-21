using System.Text;
using System.Text.Json;
using PdfObjectKit.Converters;
using PdfObjectKit.Diagnostics;
using PdfObjectKit.Extractors;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;

internal static class DiagnosticTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Diagnostics: " + name);
            }

            checks++;
        }

        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Supported"), new FixturePage("Other")));
        var stream = PdfTextExtractor.Extract(document)[1].ContentStreams.Single();
        PdfMarkdownResult Convert(string content)
        {
            document.Objects.Single(o => o.Id == stream).Value.Base64 = System.Convert.ToBase64String(Encoding.ASCII.GetBytes(content));
            return PdfMarkdownConverter.Convert(document);
        }

        var result = Convert("BT /F1 12 Tf 20 700 Td (Left) Tj 280 0 Td (Right) Tj ET");
        var diagnostic = result.Diagnostics.Single();
        Check(diagnostic.Code == PdfDiagnosticCode.AmbiguousReadingOrder, "stable layout code");
        Check(diagnostic.PageNumber == 2 && diagnostic.Severity == PdfDiagnosticSeverity.Warning, "page and severity");
        Check(diagnostic.Location == new PdfImagePoint(20, 700), "structured coordinates");
        Check(diagnostic.SourceObjects.SequenceEqual([stream]), "deduplicated source object references");
        Check(result.Markdown.Contains("Supported") && result.Markdown.Contains("Left"), "diagnostics preserve partial output");
        Check(result.Warnings.Single() == diagnostic.Message, "legacy warning projection");
        var json = JsonSerializer.Serialize(diagnostic);
        Check(json.Contains("\"AmbiguousReadingOrder\"") && json.Contains("\"Warning\""), "diagnostic enums serialize as names");
        var restored = JsonSerializer.Deserialize<PdfDiagnostic>(json)!;
        Check(restored.Code == diagnostic.Code && restored.PageNumber == 2 && restored.SourceObjects.Single() == stream, "diagnostic serialization round trip");

        result = Convert("BT /F1 12 Tf 0 1 -1 0 20 700 Tm (Rotated) Tj ET");
        Check(result.Diagnostics.Single().Code == PdfDiagnosticCode.UnsupportedTextLayout, "unsupported layout distinguished from extraction failure");
        result = Convert("BT /Missing 12 Tf (Unreadable) Tj ET");
        Check(result.Diagnostics.Any(d => d.Code == PdfDiagnosticCode.TextExtractionFailed && d.PageNumber == 2), "font failure code");
        result = Convert("q 10 0 0 10 20 20 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID abc EI Q");
        Check(result.Diagnostics.Select(d => d.Code).SequenceEqual([PdfDiagnosticCode.ImagesOmitted, PdfDiagnosticCode.OcrRequired]), "image omission and OCR are separate diagnostics");
        result = Convert("/MissingImage Do");
        Check(result.Diagnostics.Any(d => d.Code == PdfDiagnosticCode.ImageExtractionIncomplete), "image interpretation failure code");
        Check(!result.Diagnostics.Any(d => d.Code == PdfDiagnosticCode.ImagesOmitted), "unknown images not reported as known omissions");

        var structure = new PdfStructuredDocument();
        var informational = new PdfDiagnostic
        {
            Code = PdfDiagnosticCode.AmbiguousReadingOrder,
            Severity = PdfDiagnosticSeverity.Information,
            Message = "Reviewed by caller"
        };
        structure.Diagnostics.Add(informational);
        Check(structure.Warnings.Count == 0, "information excluded from warning projection");
        structure.Diagnostics.Add(informational with { Severity = PdfDiagnosticSeverity.Error });
        Check(structure.Warnings.Count == 1, "error included in warning projection");
        Check(Convert("").Diagnostics.Count == 0, "blank supported page has no spurious diagnostic");
        Console.WriteLine($"Passed {checks} typed diagnostic checks.");
    }
}
