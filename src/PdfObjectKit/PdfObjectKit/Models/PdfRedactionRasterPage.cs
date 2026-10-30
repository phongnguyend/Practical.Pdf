namespace PdfObjectKit.Models;

/// <summary>Rectangle in top-left-origin raster pixels.</summary>
public sealed record PdfRasterRedaction(int X, int Y, int Width, int Height);

/// <summary>Opaque RGB page pixels. The caller must render the entire visible page with a trusted renderer.</summary>
public sealed record PdfRedactionRasterPage(int PixelWidth, int PixelHeight, double WidthPoints, double HeightPoints,
    byte[] Rgb, IReadOnlyList<PdfRasterRedaction> Regions);
