using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Extractors;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;

internal static class MarkdownImageTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Markdown images: " + name);
            }

            checks++;
        }

        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
        var stream = PdfTextExtractor.Extract(document)[0].ContentStreams.Single();
        PdfMarkdownResult ConvertContent(string content)
        {
            document.Objects.Single(o => o.Id == stream).Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content));
            return PdfMarkdownConverter.Convert(document);
        }

        const string image = "q 10 0 0 10 20 680 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID abc EI Q\n";
        var result = ConvertContent("BT /F1 12 Tf 20 700 Td (Above) Tj 0 -30 Td (Below) Tj ET\n" + image);
        var blocks = result.Document.Pages[0].Blocks;
        Check(blocks.Select(b => b.Kind).SequenceEqual([PdfContentBlockKind.Paragraph, PdfContentBlockKind.Image, PdfContentBlockKind.Paragraph]), "image anchors between text blocks");
        var metadata = blocks[1].Image!;
        Check(metadata.Source is { IsInline: true, PixelWidth: 1, PixelHeight: 1 }, "source occurrence retained");
        var comment = "<!-- image: image-" + metadata.EncodedDataSha256.ToLowerInvariant() + ".bin -->";
        Check(result.Markdown.Contains(comment), "deterministic encoded image name");
        Check(result.Markdown.IndexOf("Above") < result.Markdown.IndexOf(comment) && result.Markdown.IndexOf(comment) < result.Markdown.IndexOf("Below"), "Markdown placement");
        Check(ConvertContent(image).Markdown == comment + "\n", "image-only page");
        Check(ConvertContent(image + image).Document.Pages[0].Blocks.Count == 2, "repeated occurrences retained");
        Check(ConvertContent(image + "BT /Missing 12 Tf (Bad) Tj ET").Markdown.Contains(comment), "image survives failed text extraction");

        metadata.FileName = @"C:\assets\photo.png";
        metadata.Caption = " A caption ";
        metadata.AltText = " Description ";
        Check(PdfMarkdownConverter.Render(result.Document).Contains("<!-- image: photo.png; caption: A caption; alt: Description -->"), "basename and optional fields");
        metadata.FileName = "/assets/evil-->.png";
        metadata.Caption = "<!--inject-->\nnew line";
        var rendered = PdfMarkdownConverter.Render(result.Document);
        Check(!rendered.Contains("<!--inject") && !rendered.Contains("evil-->") && rendered.Contains("-&#45;&gt;"), "comment delimiters escaped");
        metadata.FileName = null;
        metadata.Caption = null;
        metadata.AltText = null;
        metadata.ContentType = "image/jpeg";
        Check(PdfMarkdownConverter.Render(result.Document).Contains(".jpg -->"), "caller-specified content type extension");
        const string heading = "BT /F1 24 Tf 20 700 Td (Guideline) Tj 0 -28 Td (Github Copilot) Tj 0 -28 Td (License) Tj ET\n";
        const string rightImage = "q 100 0 0 100 300 630 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID abc EI Q\n";
        result = ConvertContent(heading + rightImage + "BT /F1 12 Tf 20 580 Td (Author) Tj ET");
        Check(result.Document.Pages[0].Blocks[0].Text == "Guideline Github Copilot License", "right-side image does not split multiline heading");
        Check(result.Markdown.IndexOf("License") < result.Markdown.IndexOf("<!-- image:")
            && result.Markdown.IndexOf("<!-- image:") < result.Markdown.IndexOf("Author"), "right-side image follows heading and precedes lower text");
        result = ConvertContent(heading.Replace("20 700", "200 700") + rightImage.Replace("300 630", "20 630"));
        Check(result.Document.Pages[0].Blocks[0].Kind == PdfContentBlockKind.Image, "left-side image precedes text");
        result = ConvertContent(heading + rightImage + image.Replace("20 680", "20 760"));
        Check(result.Document.Pages[0].Blocks[0].Kind == PdfContentBlockKind.Image
            && result.Document.Pages[0].Blocks[^1].Kind == PdfContentBlockKind.Image, "independent image anchors preserve above and beside order");
        Console.WriteLine($"Passed {checks} Markdown image checks.");
    }
}
