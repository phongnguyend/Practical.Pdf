using System.Text.Json;
using System.Text.Json.Serialization;
using PdfObjectKit.Models;

namespace PdfObjectKit.Converters;

/// <summary>Renders image metadata as a labeled, fenced JSON block.</summary>
public static class DefaultMarkDownImageRender
{
    public static string Render(PdfStructuredImage image, int pageNumber, int imageNumber)
    {
        ArgumentNullException.ThrowIfNull(image);
        var name = image.FileName?.Replace('\\', '/').Split('/')[^1].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            var extension = image.ContentType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "image/svg+xml" => ".svg",
                "image/bmp" => ".bmp",
                "image/tiff" => ".tiff",
                _ => ".bin"
            };
            var hash = image.EncodedDataSha256;
            if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            {
                throw new ArgumentException("An image without a filename requires a SHA-256 hash of its encoded data.");
            }

            name = "image-" + hash.ToLowerInvariant() + extension;
        }

        static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var occurrenceNumber = image.Source?.ImageNumber > 0 ? image.Source.ImageNumber : imageNumber;
        var json = JsonSerializer.Serialize(new
        {
            type = "image",
            fileName = name,
            pageNumber,
            imageNumber = occurrenceNumber,
            contentType = image.ContentType,
            caption = Optional(image.Caption),
            altText = Optional(image.AltText),
            ocrText = Optional(image.OcrText),
            imageUnderstandingText = Optional(image.ImageUnderstandingText)
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        var longestRun = 0;
        var run = 0;
        foreach (var character in json)
        {
            run = character == '`' ? run + 1 : 0;
            longestRun = Math.Max(longestRun, run);
        }

        var fence = new string('`', Math.Max(3, longestRun + 1));
        var label = FormattableString.Invariant($"**Image content (page {pageNumber}, image {occurrenceNumber}):**");
        return label + "\n\n" + fence + "json\n" + json + "\n" + fence;
    }

}
