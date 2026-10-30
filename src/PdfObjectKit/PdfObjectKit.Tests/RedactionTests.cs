using System.IO.Compression;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;

internal static class RedactionTests
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Raster redaction: " + message);
            }
        }
        var rgb = Enumerable.Range(0, 300).Select(index => (byte)(index % 200 + 30)).ToArray();
        var original = (byte[])rgb.Clone();
        var page = new PdfRedactionRasterPage(10, 10, 300, 500, rgb, [new(3, 3, 2, 2)]);
        var output = PdfRasterRedactor.CreateDocument([page, page with { Regions = [new(0, 0, 10, 10)] }]);
        var bytes = PdfWriter.Write(output);
        var roundTrip = PdfReader.Read(bytes);
        Check(rgb.SequenceEqual(original), "source pixels are unchanged");
        Check(PdfTextExtractor.Extract(roundTrip).All(item => string.IsNullOrWhiteSpace(item.Text)), "no hidden text");
        Check(PdfFormExtractor.Extract(roundTrip).Count == 0 && PdfAnnotationExtractor.Extract(roundTrip).Count == 0, "no forms or annotations");
        Check(roundTrip.Objects.Count == 8, "only catalog, page tree, pages, raster images and drawing streams");
        var graph = new PdfGraph(roundTrip);
        Check(graph.Pages()[0].Width == 300 && graph.Pages()[0].Height == 500, "page dimensions retained");
        var images = roundTrip.Objects.Where(item => item.Value.Entries?.GetValueOrDefault("Subtype")?.Name == "Image").ToArray();
        byte[] Decode(PdfValue value)
        {
            using var input = new MemoryStream(Convert.FromBase64String(value.Base64!));
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            zlib.CopyTo(decoded);
            return decoded.ToArray();
        }
        var actual = Decode(images[0].Value);
        for (var y = 0; y < 10; y++)
        {
            for (var x = 0; x < 10; x++)
            {
                var masked = x >= 2 && x <= 5 && y >= 2 && y <= 5;
                for (var channel = 0; channel < 3; channel++)
                {
                    var offset = (y * 10 + x) * 3 + channel;
                    Check(actual[offset] == (masked ? 0 : original[offset]), "selected pixels and safety margin removed; other pixels preserved");
                }
            }
        }
        Check(Decode(images[1].Value).All(value => value == 0), "whole-page redaction destroys all source pixels");
        using (var independent = NativeTestDocument.Open(bytes))
        {
            Check(independent.NumberOfPages == 2, "independent reader accepts output");
        }
        foreach (var invalid in new[] { page with { Rgb = [1, 2] }, page with { WidthPoints = double.NaN }, page with { Regions = [new(-1, 0, 1, 1)] }, page with { Regions = [new(9, 9, 2, 2)] } })
        {
            try
            {
                PdfRasterRedactor.CreateDocument([invalid]);
                throw new Exception("Invalid input accepted");
            }
            catch (ArgumentException)
            {
                Check(rgb.SequenceEqual(original), "rejected input leaves source unchanged");
            }
        }
        foreach (var invalidPages in new[] { System.Array.Empty<PdfRedactionRasterPage>(), Enumerable.Repeat(page, 101).ToArray() })
        {
            try
            {
                PdfRasterRedactor.CreateDocument(invalidPages);
                throw new Exception("Invalid page count accepted");
            }
            catch (ArgumentException)
            {
                Check(true, "page count validated");
            }
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            PdfRasterRedactor.CreateDocument([page], cancelled.Token);
            throw new Exception("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            Check(true, "cancellation");
        }
        Console.WriteLine("Raster redaction pixel destruction, fresh graph, preservation, validation and independent parsing passed.");
    }
}
