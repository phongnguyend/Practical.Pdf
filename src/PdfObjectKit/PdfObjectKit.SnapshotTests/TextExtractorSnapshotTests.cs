using PdfObjectKit.Extractors;
using PdfObjectKit.Readers;
using VerifyXunit;

namespace PdfObjectKit.SnapshotTests;

public class TextExtractorSnapshotTests
{
    [Fact]
    public async Task InputPdf()
    {
        var input = Path.Combine(AppContext.BaseDirectory, "Fixtures", "input.pdf");
        var document = PdfReader.Read(await File.ReadAllBytesAsync(input));
        var pages = PdfTextExtractor.Extract(document);
        Assert.Equal(4, pages.Count);
        Assert.All(pages, page =>
        {
            Assert.Null(page.Warning);
            Assert.NotEmpty(page.Text);
            Assert.NotEmpty(page.Runs!);
        });

        await Verifier.Verify(pages).UseDirectory("Snapshots").DisableDiff();
    }
}
