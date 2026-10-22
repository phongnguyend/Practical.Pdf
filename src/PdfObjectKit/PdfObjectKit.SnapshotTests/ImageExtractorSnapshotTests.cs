using PdfObjectKit.Extractors;
using PdfObjectKit.Readers;
using VerifyXunit;

namespace PdfObjectKit.SnapshotTests;

public class ImageExtractorSnapshotTests
{
    [Fact]
    public async Task ImageOccurrences()
    {
        var input = Path.Combine(AppContext.BaseDirectory, "Fixtures", "images.pdf");
        var document = PdfReader.Read(await File.ReadAllBytesAsync(input));
        var pages = PdfImageExtractor.Extract(document);
        Assert.Equal(2, pages.Count);
        Assert.All(pages, page => Assert.Null(page.Warning));
        Assert.Equal(4, pages[0].Images.Count);
        Assert.Single(pages[0].Images, image => image.IsInline);
        Assert.Equal(pages[0].Images[0].ImageObjects, pages[0].Images[1].ImageObjects);
        Assert.Empty(pages[1].Images);

        await Verifier.Verify(pages).UseDirectory("Snapshots").DontScrubDateTimes().DisableDiff();
    }
}
