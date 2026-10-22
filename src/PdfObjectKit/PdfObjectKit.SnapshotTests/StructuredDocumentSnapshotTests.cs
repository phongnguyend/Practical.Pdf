using PdfObjectKit.Analysis;
using PdfObjectKit.Readers;
using VerifyXunit;

namespace PdfObjectKit.SnapshotTests;

public class StructuredDocumentSnapshotTests
{
    [Fact]
    public async Task InputPdf()
    {
        var input = Path.Combine(AppContext.BaseDirectory, "Fixtures", "input.pdf");
        var document = PdfReader.Read(await File.ReadAllBytesAsync(input));
        var structure = PdfDocumentAnalyzer.Analyze(document);
        Assert.NotEmpty(structure.Pages);

        await Verifier.Verify(structure)
            .UseDirectory("Snapshots")
            .DontScrubDateTimes().DisableDiff();
    }
}
