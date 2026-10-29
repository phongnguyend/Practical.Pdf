namespace PdfObjectKit.Options;

public sealed class PdfTextSearchOptions
{
    public bool MatchCase { get; init; }

    public bool WholeWord { get; init; }

    public int MaxResults { get; init; } = 500;
}
