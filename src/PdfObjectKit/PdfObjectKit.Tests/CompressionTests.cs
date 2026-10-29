using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class CompressionTests
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Compression: " + message);
            }
        }
        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Keep this text")));
        PdfMetadataEditor.Update(document, new() { Fields = new() { ["Title"] = "Keep metadata" } });
        PdfFormEditor.InsertTextField(document, 1, "Name", "Field value", 20, 200, 150, 30);
        PdfAnnotationEditor.Add(document, 1, PdfAnnotationKind.Comment, [new(20, 300, 24, 24)], "Keep comment", "Reviewer");
        var graph = new PdfGraph(document);
        var catalog = graph.Resolve(document.Trailer.Entries!["Root"]);
        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(document.Objects.Max(item => item.Id.ObjectNumber) + 1);
            document.Objects.Add(new(id, value));
            return Ref(id);
        }
        var content = document.Objects.First(item => item.Value.Type == "stream" && item.Value.Entries!.GetValueOrDefault("Type")?.Name != "Metadata").Value;
        var originalContent = PdfStreamDecoder.Decode(content);
        content.Base64 = Convert.ToBase64String(originalContent.Concat(Encoding.ASCII.GetBytes(new string(' ', 10000))).ToArray());
        content.Entries!.Remove("Filter");
        content.Entries.Remove("DecodeParms");
        var unused = Add(Stream(new byte[12000]));
        // Identical image streams with different dictionary insertion order and derived lengths.
        var image = Stream(Enumerable.Repeat((byte)127, 300).ToArray());
        image.Entries!["Subtype"] = Name("Image");
        image.Entries["Width"] = Num(10);
        image.Entries["Height"] = Num(10);
        image.Entries["BitsPerComponent"] = Num(8);
        image.Entries["ColorSpace"] = Name("DeviceRGB");
        var image2 = Stream(Convert.FromBase64String(image.Base64!));
        image2.Entries = image.Entries.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        image2.Entries["Length"] = Num(300);
        catalog.Entries!["TestImages"] = Array(Add(image), Add(image2));
        // Cycles are common (page parents, fields, structure trees) and must terminate.
        var cyclic = Dict();
        var cyclicRef = Add(cyclic);
        cyclic.Entries!["Self"] = cyclicRef;
        catalog.Entries["TestCycle"] = cyclicRef;
        var before = PdfConverter.ToJson(document);
        var source = PdfWriter.Write(document);
        var originalCopy = source.ToArray();
        var result = PdfDocumentCompressor.Compress(source);
        Check(!result.UsedOriginal && result.Bytes.Length < source.Length / 2, "meaningful size reduction");
        Check(result.RemovedObjects >= 1 && result.CompressedStreams >= 1 && result.DeduplicatedResources == 1, "optimization statistics");
        Check(source.SequenceEqual(originalCopy) && PdfConverter.ToJson(document) == before, "input bytes and graph unchanged");
        var restored = PdfReader.Read(result.Bytes);
        Check(PdfTextExtractor.Extract(restored)[0].Text == "Keep this text", "text preserved");
        Check(PdfMetadataExtractor.Extract(restored).Values["Title"] == "Keep metadata", "metadata preserved");
        Check(PdfFormExtractor.Extract(restored).Single().Values.Single() == "Field value", "form value preserved");
        Check(PdfAnnotationExtractor.Extract(restored).Single().Contents == "Keep comment", "annotation preserved");
        using (var independent = NativeTestDocument.Open(result.Bytes))
        {
            Check(independent.NumberOfPages == 1, "independent reader accepts compact cross-reference sections");
        }
        Check(!restored.Objects.Any(item => item.Id == unused.Reference), "unreachable object discarded");
        graph = new PdfGraph(restored);
        var restoredImages = graph.Get(graph.Resolve(restored.Trailer.Entries!["Root"]), "TestImages")!.Items!;
        Check(restoredImages[0].Reference == restoredImages[1].Reference, "duplicate resource references redirected");
        Check(PdfStreamDecoder.Decode(graph.Resolve(restoredImages[0])).SequenceEqual(Enumerable.Repeat((byte)127, 300)), "image samples preserved");
        var second = PdfDocumentCompressor.Compress(result.Bytes);
        Check(second.UsedOriginal && second.Bytes.SequenceEqual(result.Bytes), "already compact input retained exactly");
        var disabled = PdfDocumentCompressor.Compress(source, new() { RemoveUnusedObjects = false, CompressStreams = false, DeduplicateResources = false, KeepOriginalIfLarger = false });
        Check(disabled.RemovedObjects == 0 && disabled.CompressedStreams == 0 && disabled.DeduplicatedResources == 0, "options respected");
        Check(PdfReader.Read(disabled.Bytes).Objects.Any(item => item.Id == unused.Reference), "cleanup disabled");
        var limited = PdfDocumentCompressor.Compress(source, new() { MaxDecodedBytes = 1 });
        Check(limited.Notes.Any(note => note.Contains("budget")), "decoded budget reported");

        // Predictor bytes must be decoded before replacing filters; specialized encodings remain untouched.
        var rows = Enumerable.Range(0, 128).SelectMany(_ => new byte[] { 0, 10, 20, 30, 0, 40, 50, 60 }).ToArray();
        var predicted = Stream(rows);
        using (var encoded = new MemoryStream())
        {
            using (var zip = new System.IO.Compression.ZLibStream(encoded, System.IO.Compression.CompressionLevel.NoCompression, true))
            {
                zip.Write(Convert.FromBase64String(predicted.Base64!));
            }
            predicted.Base64 = Convert.ToBase64String(encoded.ToArray());
        }
        predicted.Entries!["Filter"] = Name("FlateDecode");
        predicted.Entries["DecodeParms"] = Dict(("Predictor", Num(12)), ("Columns", Num(3)));
        catalog.Entries["Predictor"] = Add(predicted);
        var jpeg = Stream([255, 216, 255, 217]);
        jpeg.Entries!["Filter"] = Name("DCTDecode");
        catalog.Entries["Jpeg"] = Add(jpeg);
        var mixed = PdfDocumentCompressor.Compress(PdfWriter.Write(document), new() { KeepOriginalIfLarger = false });
        var mixedDocument = PdfReader.Read(mixed.Bytes);
        graph = new PdfGraph(mixedDocument);
        var mixedCatalog = graph.Resolve(mixedDocument.Trailer.Entries!["Root"]);
        var optimizedPredictor = graph.Get(mixedCatalog, "Predictor")!;
        Check(!optimizedPredictor.Entries!.ContainsKey("DecodeParms"), "predictor removed after decoding and recompression");
        Check(PdfStreamDecoder.Decode(optimizedPredictor).SequenceEqual(Enumerable.Range(0, 128).SelectMany(_ => new byte[] { 10, 20, 30, 40, 50, 60 })), "predictor samples preserved");
        Check(graph.Get(mixedCatalog, "Jpeg")!.Base64 == jpeg.Base64, "JPEG encoding unchanged");
        catalog.Entries["Signature"] = Add(Dict(("Type", Name("Sig")), ("ByteRange", Array(Num(0), Num(100), Num(200), Num(100)))));
        try
        {
            PdfDocumentCompressor.Compress(PdfWriter.Write(document));
            throw new Exception("Signature was accepted");
        }
        catch (NotSupportedException)
        {
            // Signed documents must not silently lose signature validity.
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            PdfDocumentCompressor.Compress(source, cancellationToken: cancellation.Token);
            throw new Exception("Cancellation was ignored");
        }
        catch (OperationCanceledException)
        {
            // Cancellation occurs before work begins.
        }
        Console.WriteLine("Compression size, reachability, resource sharing, fidelity, options and signature checks passed.");
    }
}
