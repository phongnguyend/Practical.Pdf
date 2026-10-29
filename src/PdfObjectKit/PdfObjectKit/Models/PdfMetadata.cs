namespace PdfObjectKit.Models;

/// <summary>Info values take precedence; missing values fall back to XMP. Dates retain their original text.</summary>
public sealed record PdfMetadata(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Warnings);

/// <summary>Omitted keys are unchanged, null removes a value. Supported keys: Title, Author, Subject,
/// Keywords, Creator, Producer, CreationDate, ModDate. Date updates require an ISO 8601 timezone.</summary>
public sealed class PdfMetadataUpdate
{
    public Dictionary<string, string?> Fields { get; init; } = [];
}
