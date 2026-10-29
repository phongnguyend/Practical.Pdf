using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using PdfObjectKit.Graphs;
using PdfObjectKit.Converters;
using PdfObjectKit.Parsers;
using System.Text;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class MetadataTests
{
    public static void Run()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception(message);
            }
        }
        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Body")));
        PdfMetadataEditor.Update(document, new() { Fields = new() { ["Title"] = "Report \u4e2d", ["Author"] = "Phong", ["CreationDate"] = "2026-10-29T09:00:00+07:00" } });
        document = PdfReader.Read(PdfWriter.Write(document));
        var values = PdfMetadataExtractor.Extract(document).Values;
        Check(values["Title"] == "Report \u4e2d" && values["Author"] == "Phong", "Unicode Info round trip");
        Check(values["CreationDate"] == "D:20261029090000+07'00'" && values.ContainsKey("ModDate"), "Date timezone preserved");
        var graph = new PdfGraph(document);
        var catalog = graph.Resolve(document.Trailer.Entries!["Root"]);
        var stream = graph.Get(catalog, "Metadata")!;
        var xml = Encoding.UTF8.GetString(PdfStreamDecoder.Decode(stream));
        Check(xml.Contains("Report \u4e2d") && xml.Contains("2026-10-29T09:00:00"), "XMP synchronized");
        var customXml = System.Xml.Linq.XDocument.Parse(xml);
        customXml.Descendants(System.Xml.Linq.XName.Get("Description", "http://www.w3.org/1999/02/22-rdf-syntax-ns#")).First().Add(new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("Custom", "urn:metadata-test"), "Untouched"));
        stream.Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(customXml.ToString()));
        var info = graph.Get(document.Trailer, "Info")!;
        info.Entries!["Custom"] = Str("Keep me"u8.ToArray());
        info.Entries["CreationDate"] = Str("D:2026"u8.ToArray());
        PdfMetadataEditor.Update(document, new() { Fields = new() { ["Author"] = null } });
        values = PdfMetadataExtractor.Extract(document).Values;
        Check(!values.ContainsKey("Author") && values["Custom"] == "Keep me" && values["CreationDate"] == "D:2026", "Removal and untouched partial dates/custom fields");
        var updatedGraph = new PdfGraph(document);
        var updatedXmp = updatedGraph.Get(updatedGraph.Resolve(document.Trailer.Entries!["Root"]), "Metadata")!;
        Check(Encoding.UTF8.GetString(PdfStreamDecoder.Decode(updatedXmp)).Contains("Untouched"), "Unrelated XMP properties preserved");
        var before = PdfConverter.ToJson(document);
        try
        {
            PdfMetadataEditor.Update(document, new() { Fields = new() { ["CreationDate"] = "not a date" } });
            throw new Exception("Invalid date accepted");
        }
        catch (ArgumentException)
        {
            Check(before == PdfConverter.ToJson(document), "Invalid update is atomic");
        }
        var xmpOnly = PdfReader.Read(PdfWriter.Write(document));
        xmpOnly.Trailer.Entries!.Remove("Info");
        Check(PdfMetadataExtractor.Extract(xmpOnly).Values["Title"] == "Report \u4e2d", "XMP fallback");
        graph = new PdfGraph(document);
        graph.Get(graph.Resolve(document.Trailer.Entries!["Root"]), "Metadata")!.Base64 = Convert.ToBase64String("not XML"u8.ToArray());
        Check(PdfMetadataExtractor.Extract(document).Warnings.Count == 1, "Malformed XMP does not prevent Info extraction");
        Console.WriteLine("Metadata extraction, synchronization, dates and atomicity checks passed.");
    }
}
