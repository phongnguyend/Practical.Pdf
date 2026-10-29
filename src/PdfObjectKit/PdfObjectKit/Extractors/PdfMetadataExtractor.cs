using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Metadata;

namespace PdfObjectKit.Extractors;

public static class PdfMetadataExtractor
{
    public static PdfMetadata Extract(PdfObjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var graph = new PdfGraph(document);
        var values = new Dictionary<string, string>();
        var warnings = new List<string>();
        if (graph.Get(document.Trailer, "Info") is { Entries: { } entries })
        {
            foreach (var pair in entries)
            {
                var value = graph.Resolve(pair.Value);
                if (value.Type == "string")
                {
                    values[pair.Key] = PdfMetadataCodec.Decode(value);
                }
            }
        }
        try
        {
            var xml = PdfMetadataCodec.ReadXmp(graph, graph.Resolve(document.Trailer.Entries!["Root"]));
            foreach (var pair in PdfMetadataCodec.Names)
            {
                if (PdfMetadataCodec.Value(xml, pair.Value) is { } value)
                {
                    if (values.TryGetValue(pair.Key, out var existing))
                    {
                        if (existing != value && pair.Key is not ("CreationDate" or "ModDate"))
                        {
                            warnings.Add($"{pair.Key} differs between Info and XMP; showing Info.");
                        }
                    }
                    else
                    {
                        values[pair.Key] = value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException or NotSupportedException)
        {
            warnings.Add("XMP metadata could not be read: " + ex.Message);
        }
        return new(values, warnings);
    }
}
