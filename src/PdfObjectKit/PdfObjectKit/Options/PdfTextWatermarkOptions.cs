namespace PdfObjectKit.Options;

public enum PdfWatermarkPosition { Center, TopLeft, TopRight, BottomLeft, BottomRight }

public enum PdfWatermarkLayer { Foreground, Background }

/// <summary>Single-line Helvetica watermark. Placement is relative to the rotated visible page.</summary>
public sealed record PdfTextWatermarkOptions
{
    public string Text { get; init; } = "CONFIDENTIAL";

    /// <summary>One-based page numbers. Null selects all pages.</summary>
    public IReadOnlyList<int>? PageNumbers { get; init; }

    public double FontSize { get; init; } = 48;

    public double Opacity { get; init; } = 0.2;

    /// <summary>Counterclockwise angle in the displayed page.</summary>
    public double Rotation { get; init; } = 45;

    public string Color { get; init; } = "#808080";

    public double Margin { get; init; } = 24;

    public PdfWatermarkPosition Position { get; init; } = PdfWatermarkPosition.Center;

    public PdfWatermarkLayer Layer { get; init; } = PdfWatermarkLayer.Foreground;
}
