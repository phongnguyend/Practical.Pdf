using PdfObjectKit.Converters;
using PdfObjectKit.Readers;
using VerifyXunit;

namespace PdfObjectKit.SnapshotTests;

public class MarkdownSnapshotTests
{
    [Fact]
    public async Task InputPdf()
    {
        var input = Path.Combine(AppContext.BaseDirectory, "Fixtures", "input.pdf");
        var document = PdfReader.Read(await File.ReadAllBytesAsync(input));
        var result = PdfMarkdownConverter.Convert(document);
        Assert.False(string.IsNullOrWhiteSpace(result.Markdown));

        await Verifier.Verify(result.Markdown, extension: "md")
            .UseDirectory("Snapshots")
            .DontScrubDateTimes().DisableDiff();
    }
}
