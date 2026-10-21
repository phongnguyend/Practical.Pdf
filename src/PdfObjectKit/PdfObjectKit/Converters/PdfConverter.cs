using PdfObjectKit.Graphs;
using PdfObjectKit.Extractors;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Validators;
using PdfObjectKit.Models;
using PdfObjectKit.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfObjectKit.Converters;

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

    /// <summary>Exports a fresh text index separately from the object graph.</summary>
    public static string ToTextsJson(PdfObjectDocument document)
    {
        PdfDocumentValidator.Validate(document);
        return JsonSerializer.Serialize(new { Pages = PdfTextExtractor.Extract(document) }, Options);
    }

    /// <summary>Exports a fresh image index separately from the object graph.</summary>
    public static string ToImagesJson(PdfObjectDocument document)
    {
        PdfDocumentValidator.Validate(document);
        return JsonSerializer.Serialize(new { Pages = PdfImageExtractor.Extract(document) }, Options);
    }

    /// <summary>Exports derived paragraphs separately, with a warning for unsupported pages.</summary>
    public static string ToParagraphsJson(PdfObjectDocument document, PdfHeuristicOptions? heuristics = null)
    {
        PdfDocumentValidator.Validate(document);
        var h = heuristics ?? new();
        h.Validate();
        var graph = new PdfGraph(document);
        var pages = graph.Pages().Select(page =>
        {
            List<PdfParagraph>? paragraphs = null;
            string? warning = null;
            try
            {
                paragraphs = PdfParagraphExtractor.Extract(page, new PdfPageInterpreter(graph, page), h.TextColumnGapFactor, h);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                warning = ex.Message;
            }

            return new { PageNumber = page.Number, Paragraphs = paragraphs, Warning = warning };
        }).ToList();
        return JsonSerializer.Serialize(new { Pages = pages }, Options);
    }

    public static PdfObjectDocument FromJson(string json)
    {
        var document = JsonSerializer.Deserialize<PdfObjectDocument>(json, Options) ?? throw new InvalidDataException("Expected a PDF object document.");
        PdfDocumentValidator.Validate(document);
        return document;
    }
}
