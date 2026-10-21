using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfObjectKit;

/// <summary>Converts PDF object graphs to and from their JSON representation.</summary>
public static class PdfConverter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 256
    };

    public static string ToJson(PdfObjectDocument document)
    {
        PdfDocumentValidator.Validate(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static PdfObjectDocument FromJson(string json)
    {
        var document = JsonSerializer.Deserialize<PdfObjectDocument>(json, Options) ?? throw new InvalidDataException("Expected a PDF object document.");
        PdfDocumentValidator.Validate(document);
        return document;
    }
}
