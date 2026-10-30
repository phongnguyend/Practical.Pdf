using System.IO.Compression;
using System.Text;
using PdfObjectKit.Models;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

/// <summary>Builds a fresh image-only PDF from rendered pages, overwriting marked pixels before encoding.
/// This does not render PDFs, detect secrets, or prove the caller selected every sensitive region.</summary>
public static class PdfRasterRedactor
{
    public const long MaxRasterBytes = 64L * 1024 * 1024;

    public static PdfObjectDocument CreateDocument(IEnumerable<PdfRedactionRasterPage> pages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var kids = Array();
        var tree = Dict(("Type", Name("Pages")), ("Kids", kids), ("Count", Num(0)));
        var result = new PdfObjectDocument
        {
            Trailer = Dict(("Root", Ref(new(1)))),
            Objects = [new(new(1), Dict(("Type", Name("Catalog")), ("Pages", Ref(new(2))))), new(new(2), tree)]
        };
        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(result.Objects.Count + 1);
            result.Objects.Add(new(id, value));
            return Ref(id);
        }
        long total = 0;
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(page);
            if (kids.Items!.Count >= 100 || page.PixelWidth <= 0 || page.PixelHeight <= 0
                || (long)page.PixelWidth * page.PixelHeight > 16000000
                || !double.IsFinite(page.WidthPoints) || !double.IsFinite(page.HeightPoints)
                || page.WidthPoints <= 0 || page.HeightPoints <= 0 || page.WidthPoints > 14400 || page.HeightPoints > 14400
                || page.Rgb is null || page.Rgb.LongLength != (long)page.PixelWidth * page.PixelHeight * 3
                || page.Regions is null || page.Regions.Count > 1000)
            {
                throw new ArgumentException("Invalid raster page, dimensions, pixel data or redaction count.", nameof(pages));
            }
            total += page.Rgb.LongLength;
            if (total > MaxRasterBytes)
            {
                throw new ArgumentException("Raster pages exceed the 64 MB pixel limit.", nameof(pages));
            }
            var pixels = (byte[])page.Rgb.Clone();
            foreach (var region in page.Regions)
            {
                if (region is null || region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
                    || (long)region.X + region.Width > page.PixelWidth || (long)region.Y + region.Height > page.PixelHeight)
                {
                    throw new ArgumentException("Redaction rectangles must lie inside the raster page.", nameof(pages));
                }
                // Expand one pixel to cover antialiased edges at the selection boundary.
                var left = Math.Max(0, region.X - 1);
                var right = Math.Min(page.PixelWidth, region.X + region.Width + 1);
                var bottom = Math.Min(page.PixelHeight, region.Y + region.Height + 1);
                for (var y = Math.Max(0, region.Y - 1); y < bottom; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    System.Array.Clear(pixels, (y * page.PixelWidth + left) * 3, (right - left) * 3);
                }
            }
            using var compressed = new MemoryStream();
            using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(pixels);
            }
            var image = Stream(compressed.ToArray());
            image.Entries!["Type"] = Name("XObject");
            image.Entries["Subtype"] = Name("Image");
            image.Entries["Width"] = Num(page.PixelWidth);
            image.Entries["Height"] = Num(page.PixelHeight);
            image.Entries["ColorSpace"] = Name("DeviceRGB");
            image.Entries["BitsPerComponent"] = Num(8);
            image.Entries["Filter"] = Name("FlateDecode");
            var imageRef = Add(image);
            var content = Add(Stream(Encoding.ASCII.GetBytes($"q {Syntax(Num(page.WidthPoints))} 0 0 {Syntax(Num(page.HeightPoints))} 0 0 cm /PageImage Do Q")));
            kids.Items.Add(Add(Dict(("Type", Name("Page")), ("Parent", Ref(new(2))),
                ("MediaBox", Array(Num(0), Num(0), Num(page.WidthPoints), Num(page.HeightPoints))),
                ("Resources", Dict(("XObject", Dict(("PageImage", imageRef))))), ("Contents", content))));
        }
        if (kids.Items!.Count == 0)
        {
            throw new ArgumentException("At least one rendered page is required.", nameof(pages));
        }
        tree.Entries!["Count"] = Num(kids.Items.Count);
        return result;
    }
}
