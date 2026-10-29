using System.Text;
using System.Xml;
using System.Xml.Linq;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;

namespace PdfObjectKit.Metadata;

internal static class PdfMetadataCodec
{
    internal static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    internal static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    internal static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";
    internal static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    internal static readonly Dictionary<string, XName> Names = new()
    {
        ["Title"] = Dc + "title", ["Author"] = Dc + "creator", ["Subject"] = Dc + "description",
        ["Keywords"] = Pdf + "Keywords", ["Creator"] = Xmp + "CreatorTool", ["Producer"] = Pdf + "Producer",
        ["CreationDate"] = Xmp + "CreateDate", ["ModDate"] = Xmp + "ModifyDate"
    };

    internal static string Decode(PdfValue value)
    {
        var bytes = Convert.FromBase64String(value.Base64!);
        if (bytes.AsSpan().StartsWith(new byte[] { 254, 255 }))
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }))
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        const string accents = "\u02d8\u02c7\u02c6\u02d9\u02dd\u02db\u02da\u02dc";
        const string symbols = "\u2022\u2020\u2021\u2026\u2014\u2013\u0192\u2044\u2039\u203a\u2212\u2030\u201e\u201c\u201d\u2018\u2019\u201a\u2122\ufb01\ufb02\u0141\u0152\u0160\u0178\u017d\u0131\u0142\u0153\u0161\u017e";
        return new string(bytes.Select(b => b is >= 24 and <= 31 ? accents[b - 24] : b is >= 128 and <= 158 ? symbols[b - 128] : b == 160 ? '\u20ac' : (char)b).ToArray());
    }

    internal static XDocument ReadXmp(PdfGraph graph, PdfValue catalog)
    {
        var stream = graph.Get(catalog, "Metadata");
        if (stream is null)
        {
            return new XDocument(new XElement(XName.Get("xmpmeta", "adobe:ns:meta/"), new XElement(Rdf + "RDF", new XElement(Rdf + "Description", new XAttribute(Rdf + "about", "")))));
        }
        if (stream.Type != "stream")
        {
            throw new InvalidDataException("Catalog metadata must be an XML stream.");
        }
        using var data = new MemoryStream(PdfStreamDecoder.Decode(stream));
        using var reader = XmlReader.Create(data, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2000000 });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    internal static string? Value(XDocument xml, XName name)
    {
        foreach (var description in xml.Descendants(Rdf + "Description"))
        {
            if (description.Attribute(name) is { } attribute)
            {
                return attribute.Value;
            }
            if (description.Element(name) is { } property)
            {
                var items = property.Descendants(Rdf + "li").ToList();
                return items.Count == 0 ? property.Value : items.FirstOrDefault(item => (string?)item.Attribute(XNamespace.Xml + "lang") == "x-default")?.Value ?? string.Join("; ", items.Select(item => item.Value));
            }
        }
        return null;
    }
}
