namespace PdfObjectKit.Editors;

public sealed class PdfTextEditOptions
{
    /// <summary>Optional TrueType font to embed when the original font cannot encode an edit.</summary>
    public byte[]? FallbackFont { get; init; }
}
