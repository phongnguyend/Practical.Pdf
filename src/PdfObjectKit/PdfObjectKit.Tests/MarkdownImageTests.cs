using System.Text;
using System.Text.Json;
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
        var renderCalls = 0;
        var custom = new PdfMarkdownOptions
        {
            ImageRender = (imageData, page, number) =>
            {
                renderCalls++;
                Check(imageData.Source is not null && page == 1 && number == 1, "custom renderer context");
                return "Custom image content";
            }
        };
        Check(PdfMarkdownConverter.Render(result.Document, custom) == "Above\n\nCustom image content\n\nBelow\n", "custom render output and placement");
        Check(PdfMarkdownConverter.Convert(document, custom).Markdown.Contains("Custom image content") && renderCalls == 2, "Convert forwards render options");
        Check(PdfMarkdownConverter.Render(result.Document, new PdfMarkdownOptions { ImageRender = (_, _, _) => "" }) == "Above\n\nBelow\n", "omit images without extra whitespace");
        Check(PdfMarkdownConverter.Render(result.Document, new PdfMarkdownOptions()) == result.Markdown, "default renderer unchanged");
        Check(metadata.Source is { IsInline: true, PixelWidth: 1, PixelHeight: 1 }, "source occurrence retained");
        JsonElement ImageJson(string markdown)
        {
            var start = markdown.IndexOf("json\n", StringComparison.Ordinal) + 5;
            var end = markdown.IndexOf("\n```", start, StringComparison.Ordinal);
            return JsonDocument.Parse(markdown[start..end]).RootElement.Clone();
        }

        var payload = ImageJson(result.Markdown);
        Check(payload.GetProperty("fileName").GetString() == "image-" + metadata.EncodedDataSha256.ToLowerInvariant() + ".bin", "deterministic name");
        Check(payload.GetProperty("pageNumber").GetInt32() == 1 && payload.GetProperty("imageNumber").GetInt32() == 1, "occurrence identifiers");
        Check(result.Markdown.Contains("**Image content (page 1, image 1):**\n\n```json"), "image label precedes JSON fence");
        Check(ConvertContent(image + image).Markdown.Contains("**Image content (page 1, image 2):**"), "label identifies repeated occurrence");
        Check(result.Markdown.IndexOf("Above") < result.Markdown.IndexOf("```json") && result.Markdown.IndexOf("```json") < result.Markdown.IndexOf("Below"), "Markdown placement");
        Check(ImageJson(ConvertContent(image).Markdown).GetProperty("type").GetString() == "image", "image-only page");
        Check(ConvertContent(image + image).Markdown.Contains("\"imageNumber\": 2"), "repeated occurrences identified");
        Check(ImageJson(ConvertContent(image + "BT /Missing 12 Tf (Bad) Tj ET").Markdown).GetProperty("type").GetString() == "image", "image survives failed text extraction");

        metadata.FileName = @"C:\assets\photo.png";
        metadata.Caption = " A caption ";
        metadata.AltText = " Description ";
        metadata.OcrText = "Invoice 123\nTotal: \"100\"\n```json\n<script>";
        metadata.ImageUnderstandingText = " A scanned invoice ";
        payload = ImageJson(PdfMarkdownConverter.Render(result.Document));
        Check(payload.GetProperty("fileName").GetString() == "photo.png", "basename");
        Check(payload.GetProperty("caption").GetString() == "A caption" && payload.GetProperty("altText").GetString() == "Description", "trimmed metadata");
        Check(payload.GetProperty("ocrText").GetString() == metadata.OcrText, "multiline and syntax round trip");
        Check(payload.GetProperty("imageUnderstandingText").GetString() == "A scanned invoice", "understanding");
        metadata.OcrText = " \t\n";
        metadata.ImageUnderstandingText = null;
        payload = ImageJson(PdfMarkdownConverter.Render(result.Document));
        Check(!payload.TryGetProperty("ocrText", out _) && !payload.TryGetProperty("imageUnderstandingText", out _), "blank fields omitted");
        metadata.FileName = null;
        metadata.ContentType = "image/jpeg";
        Check(ImageJson(PdfMarkdownConverter.Render(result.Document)).GetProperty("fileName").GetString()!.EndsWith(".jpg"), "content type extension");
        const string heading = "BT /F1 24 Tf 20 700 Td (Guideline) Tj 0 -28 Td (Github Copilot) Tj 0 -28 Td (License) Tj ET\n";
        const string rightImage = "q 100 0 0 100 300 630 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID abc EI Q\n";
        result = ConvertContent(heading + rightImage + "BT /F1 12 Tf 20 580 Td (Author) Tj ET");
        Check(result.Document.Pages[0].Blocks[0].Text == "Guideline Github Copilot License", "right-side image does not split multiline heading");
        Check(result.Markdown.IndexOf("License") < result.Markdown.IndexOf("```json")
            && result.Markdown.IndexOf("```json") < result.Markdown.IndexOf("Author"), "right-side image follows heading and precedes lower text");
        result = ConvertContent(heading.Replace("20 700", "200 700") + rightImage.Replace("300 630", "20 630"));
        Check(result.Document.Pages[0].Blocks[0].Kind == PdfContentBlockKind.Image, "left-side image precedes text");
        result = ConvertContent(heading + rightImage + image.Replace("20 680", "20 760"));
        Check(result.Document.Pages[0].Blocks[0].Kind == PdfContentBlockKind.Image
            && result.Document.Pages[0].Blocks[^1].Kind == PdfContentBlockKind.Image, "independent image anchors preserve above and beside order");
        Console.WriteLine($"Passed {checks} Markdown image checks.");
    }
}
