using System.Globalization;
using System.Text;
using System.Xml.Linq;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Metadata;
using PdfObjectKit.Extractors;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

public static class PdfMetadataEditor
{
    public static void Update(PdfObjectDocument document, PdfMetadataUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(update.Fields);
        if (update.Fields.Count == 0)
        {
            return;
        }
        PdfPageEditor.Edit(document, working =>
        {
            if (PdfFormExtractor.Extract(working).Any(field => field.IsSigned))
            {
                throw new NotSupportedException("Metadata editing would invalidate existing digital signatures.");
            }
            var graph = new PdfGraph(working);
            var catalog = graph.Resolve(working.Trailer.Entries!["Root"]);
            var xml = PdfMetadataCodec.ReadXmp(graph, catalog);
            var rdf = xml.Descendants(PdfMetadataCodec.Rdf + "RDF").SingleOrDefault() ?? throw new InvalidDataException("XMP must contain one RDF element.");
            var description = rdf.Elements(PdfMetadataCodec.Rdf + "Description").FirstOrDefault();
            if (description is null)
            {
                description = new XElement(PdfMetadataCodec.Rdf + "Description", new XAttribute(PdfMetadataCodec.Rdf + "about", ""));
                rdf.Add(description);
            }
            var info = graph.Get(working.Trailer, "Info") ?? Dict();
            if (info.Type != "dictionary")
            {
                throw new InvalidDataException("Info must be a dictionary.");
            }
            info = new PdfValue { Type = "dictionary", Entries = new(info.Entries!) };
            var changes = new Dictionary<string, string?>(update.Fields);
            changes.TryAdd("ModDate", DateTimeOffset.UtcNow.ToString("O"));
            foreach (var pair in changes)
            {
                if (!PdfMetadataCodec.Names.TryGetValue(pair.Key, out var property) || pair.Value?.Length > 10000)
                {
                    throw new ArgumentException("Unknown metadata field or value longer than 10000 characters.");
                }
                var value = pair.Value;
                var infoValue = value;
                if (value is not null && pair.Key is "CreationDate" or "ModDate")
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"T.*(?:Z|[+-]\d{2}:\d{2})$")
                        || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        throw new ArgumentException("Date updates require ISO 8601 with a timezone, for example 2026-10-29T09:00:00+07:00.");
                    }
                    value = date.ToString("O");
                    infoValue = "D:" + date.ToString("yyyyMMddHHmmsszzz", CultureInfo.InvariantCulture).Replace(":", "'") + "'";
                }
                if (value is null)
                {
                    info.Entries!.Remove(pair.Key);
                }
                else
                {
                    info.Entries![pair.Key] = Str([254, 255, .. Encoding.BigEndianUnicode.GetBytes(infoValue!)]);
                }
                var translations = pair.Key is "Title" or "Subject"
                    ? rdf.Elements(PdfMetadataCodec.Rdf + "Description").Elements(property).Elements(PdfMetadataCodec.Rdf + "Alt")
                        .Elements(PdfMetadataCodec.Rdf + "li").Where(item => (string?)item.Attribute(XNamespace.Xml + "lang") != "x-default").Select(item => new XElement(item)).ToList()
                    : [];
                foreach (var node in rdf.Elements(PdfMetadataCodec.Rdf + "Description"))
                {
                    node.Attributes(property).Remove();
                    node.Elements(property).Remove();
                }
                if (value is not null)
                {
                    object content = pair.Key is "Title" or "Subject" ? new XElement(PdfMetadataCodec.Rdf + "Alt", new XElement(PdfMetadataCodec.Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), value), translations)
                        : pair.Key == "Author" ? new XElement(PdfMetadataCodec.Rdf + "Seq", new XElement(PdfMetadataCodec.Rdf + "li", value)) : value;
                    description.Add(new XElement(property, content));
                }
            }
            PdfValue Add(PdfValue value)
            {
                var id = new PdfReference(checked(working.Objects.Max(obj => obj.Id.ObjectNumber) + 1));
                working.Objects.Add(new(id, value));
                return Ref(id);
            }
            working.Trailer.Entries!["Info"] = Add(info);
            var metadata = Stream(Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));
            metadata.Entries!["Type"] = Name("Metadata");
            metadata.Entries["Subtype"] = Name("XML");
            catalog.Entries!["Metadata"] = Add(metadata);
        });
    }
}
