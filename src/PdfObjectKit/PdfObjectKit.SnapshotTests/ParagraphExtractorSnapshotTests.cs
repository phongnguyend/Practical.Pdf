using PdfObjectKit.Extractors;
using PdfObjectKit.Readers;
using VerifyXunit;

namespace PdfObjectKit.SnapshotTests;

public class ParagraphExtractorSnapshotTests
{
    [Fact]
    public async Task InputPdf()
    {
        var input = Path.Combine(AppContext.BaseDirectory, "Fixtures", "input.pdf");
        var document = PdfReader.Read(await File.ReadAllBytesAsync(input));
        // The stable input fixture contains four pages. Keep page boundaries explicit.
        var pages = Enumerable.Range(1, 4).Select(pageNumber => new
        {
            PageNumber = pageNumber,
            Paragraphs = PdfParagraphExtractor.Extract(document, pageNumber)
        }).ToList();
        Assert.All(pages, page => Assert.NotEmpty(page.Paragraphs));

        await Verifier.Verify(pages).UseDirectory("Snapshots").DisableDiff();
    }
}
