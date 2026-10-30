using PdfObjectKit.Interpreters;
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

internal static class ContentRedactionTests
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Content redaction: " + message);
            }
        }
        PdfObjectDocument Fixture(string content)
        {
            var doc = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Placeholder")));
            var stream = new PdfGraph(doc).Pages()[0].Streams[0].Value;
            stream.Entries!.Clear();
            stream.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content));
            return doc;
        }
        var source = Fixture("/Span << /ActualText (HIDDEN SECRET) >> BDC BT /F1 12 Tf 20 100 Td (SECRET) Tj 0 -60 Td (PUBLIC) Tj ET EMC 200 200 30 30 re f 300 300 20 20 re f");
        PdfMetadataEditor.Update(source, new() { Fields = new() { ["Title"] = "SECRET TITLE" } });
        var before = PdfConverter.ToJson(source);
        var redacted = PdfContentRedactor.Apply(source, [new(1, 10, 90, 100, 30), new(1, 205, 205, 10, 10)]);
        var bytes = PdfWriter.Write(redacted);
        var text = PdfTextExtractor.Extract(PdfReader.Read(bytes))[0].Text;
        Check(!text.Contains("SECRET") && text.Contains("PUBLIC"), "sensitive text removed, unrelated text searchable");
        Check(PdfConverter.ToJson(source) == before, "source unchanged");
        var graph = new PdfGraph(redacted);
        var operations = PdfContent.Parse(PdfStreamDecoder.Decode(graph.Pages()[0].Streams[0].Value));
        Check(!operations.Any(op => op.Operator == "re" && Number(op.Operands[0]) == 200), "intersecting vector removed");
        Check(operations.Any(op => op.Operator == "re" && Number(op.Operands[0]) == 300), "unrelated vector retained");
        Check(!Encoding.Latin1.GetString(bytes).Contains("HIDDEN") && !Encoding.Latin1.GetString(bytes).Contains("TITLE"), "marked-content alternate text and metadata removed");
        using (var independent = NativeTestDocument.Open(bytes))
        {
            Check(independent.GetPage(1).Text.Contains("PUBLIC"), "independent parse");
        }
        var partial = Fixture("BT /F1 12 Tf 20 100 Td [(SECRET) -10000 (PUBLIC)] TJ ET");
        var originalPublic = new PdfPageInterpreter(new PdfGraph(partial), new PdfGraph(partial).Pages()[0]).Slots.Last().BaselineStart;
        var partialResult = PdfContentRedactor.Apply(partial, [new(1, 10, 90, 100, 30)]);
        var partialGraph = new PdfGraph(partialResult);
        var publicSlot = new PdfPageInterpreter(partialGraph, partialGraph.Pages()[0]).Slots.First(slot => slot.Text == "P");
        Check(Math.Abs(originalPublic.X - publicSlot.BaselineStart.X) < .000001, "remaining text advance preserved");
        var fontBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "RenderingTest.ttf"));
        var embedded = PdfReader.Read(PdfFixture.Create(fontBytes, new FixturePage("AB", X: 20, Y: 100)));
        var embeddedBefore = PdfConverter.ToJson(embedded);
        var embeddedResult = PdfContentRedactor.Apply(embedded, [new(1, 0, 70, 300, 80)]);
        Check(string.IsNullOrWhiteSpace(PdfTextExtractor.Extract(embeddedResult)[0].Text), "fully redacted embedded-font text is removed");
        Check(!PdfConverter.ToJson(embeddedResult).Contains("FontFile2") && !PdfConverter.ToJson(embeddedResult).Contains("ToUnicode"), "discarded embedded font and mappings are not retained");
        Check(PdfConverter.ToJson(embedded) == embeddedBefore, "embedded source remains unchanged");
        var retainedEmbedded = PdfContentRedactor.Apply(embedded, [new(1, 400, 400, 20, 20)]);
        Check(PdfTextExtractor.Extract(retainedEmbedded)[0].Text.Contains("AB"), "remaining embedded TrueType text is supported");
        var partialEmbedded = PdfReader.Read(PdfFixture.Create(fontBytes, new FixturePage("AB")));
        var embeddedGraph = new PdfGraph(partialEmbedded);
        embeddedGraph.Pages()[0].Streams[0].Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("BT /F1 12 Tf 20 100 Td [<0001> -10000 <0002>] TJ ET"));
        embeddedGraph.Pages()[0].Streams[0].Value.Entries!.Remove("Filter");
        var embeddedPosition = new PdfPageInterpreter(embeddedGraph, embeddedGraph.Pages()[0]).Slots.Last().BaselineStart;
        var subsetResult = PdfContentRedactor.Apply(partialEmbedded, [new(1, 0, 80, 50, 50)]);
        var subsetGraph = new PdfGraph(subsetResult);
        var subsetSlots = new PdfPageInterpreter(subsetGraph, subsetGraph.Pages()[0]).Slots;
        Check(subsetSlots.Count == 1 && subsetSlots[0].Text == "B" && Math.Abs(subsetSlots[0].BaselineStart.X - embeddedPosition.X) < .000001, "embedded subset keeps glyph identity and placement");
        var subsetFont = subsetGraph.Get(subsetGraph.Pages()[0].Resources, "Font")!.Entries!.Values.Single();
        var subsetMap = Encoding.ASCII.GetString(PdfStreamDecoder.Decode(subsetGraph.Get(subsetFont, "ToUnicode")!));
        Check(!subsetMap.Contains("<0041>") && subsetMap.Contains("<0042>"), "only retained Unicode mappings survive");
        var subsetMetric = subsetGraph.Resolve(subsetGraph.Get(subsetFont, "DescendantFonts")!.Items![0]);
        var subsetFile = PdfStreamDecoder.Decode(subsetGraph.Get(subsetGraph.Get(subsetMetric, "FontDescriptor")!, "FontFile2")!);
        var tables = new Dictionary<string, byte[]>();
        for (var index = 0; index < System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(subsetFile.AsSpan(4)); index++)
        {
            var record = 12 + index * 16;
            var offset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(subsetFile.AsSpan(record + 8));
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(subsetFile.AsSpan(record + 12));
            tables.Add(Encoding.ASCII.GetString(subsetFile, record, 4), subsetFile.AsSpan(offset, length).ToArray());
        }
        var locationA = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(tables["loca"].AsSpan(4));
        var locationAfterA = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(tables["loca"].AsSpan(8));
        Check(locationA == locationAfterA, "removed A outline is absent from embedded font");
        Check(!tables.ContainsKey("GSUB") && !tables.ContainsKey("prep") && !tables.ContainsKey("fpgm"), "source shaping and hint programs discarded");
        using (var independent = NativeTestDocument.Open(PdfWriter.Write(subsetResult)))
        {
            Check(independent.GetPage(1).Text.Trim() == "B", "independent parser accepts subset output");
        }
        static void Set16(byte[] bytes, int offset, int value)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), (ushort)value);
        }
        static void Set32(byte[] bytes, int offset, int value)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)value);
        }
        var compoundTables = tables.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        Set16(compoundTables["maxp"], 4, 4);
        Set16(compoundTables["hhea"], 34, 4);
        compoundTables["hmtx"] = new byte[16];
        compoundTables["loca"] = new byte[20];
        Set32(compoundTables["loca"], 8, 16);
        Set32(compoundTables["loca"], 12, 40);
        Set32(compoundTables["loca"], 16, 56);
        compoundTables["glyf"] = new byte[56];
        Set16(compoundTables["glyf"], 0, 1);
        compoundTables["glyf"][14] = 0x31;
        Set16(compoundTables["glyf"], 16, 65535);
        Set16(compoundTables["glyf"], 26, 0x103);
        Set16(compoundTables["glyf"], 28, 1);
        Set16(compoundTables["glyf"], 34, 1);
        compoundTables["glyf"][36] = 0x42;
        compoundTables["SECR"] = Encoding.ASCII.GetBytes("SECRET");
        var cyclicTables = compoundTables.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        Set16(cyclicTables["glyf"], 28, 2);
        PdfObjectKit.Fonts.PdfRedactionFontSubset.Apply(compoundTables, [2]);
        Check(compoundTables["glyf"].Length == 36 && !compoundTables.ContainsKey("SECR"), "composite dependencies retained, unused glyphs and arbitrary tables removed");
        Check((System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(compoundTables["glyf"].AsSpan(26)) & 256) == 0, "composite hint flag cleared");
        try
        {
            PdfObjectKit.Fonts.PdfRedactionFontSubset.Apply(cyclicTables, [2]);
            throw new Exception("Cyclic font accepted");
        }
        catch (InvalidDataException)
        {
            Check(true, "cyclic composite rejected");
        }
        var simpleDocument = PdfReader.Read(PdfFixture.Create(fontBytes, new FixturePage("AB")));
        var simpleGraph = new PdfGraph(simpleDocument);
        var originalFont = simpleGraph.Get(simpleGraph.Pages()[0].Resources, "Font")!.Entries!["F1"];
        var originalMetric = simpleGraph.Resolve(simpleGraph.Get(originalFont, "DescendantFonts")!.Items![0]);
        var simpleFont = Dict(("Type", Name("Font")), ("Subtype", Name("TrueType")), ("BaseFont", Name("BCDEEE+ArialMT")),
            ("Encoding", Name("WinAnsiEncoding")), ("FirstChar", Num(65)), ("LastChar", Num(66)),
            ("Widths", PdfObjectKit.Primitives.PdfPrimitives.Array(Num(600), Num(600))),
            ("FontDescriptor", simpleGraph.Get(originalMetric, "FontDescriptor")!));
        simpleGraph.Get(simpleGraph.Pages()[0].Resources, "Font")!.Entries!["F1"] = simpleFont;
        simpleGraph.Pages()[0].Streams[0].Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("BT /F1 12 Tf 20 100 Td (AB) Tj ET q 20 0 0 20 300 300 cm /Photo Do Q"));
        simpleGraph.Pages()[0].Streams[0].Value.Entries!.Remove("Filter");
        var photo = Stream(new byte[] { 255, 0, 0 });
        photo.Entries!["Subtype"] = Name("Image");
        photo.Entries["Width"] = Num(1);
        photo.Entries["Height"] = Num(1);
        photo.Entries["BitsPerComponent"] = Num(8);
        photo.Entries["ColorSpace"] = Name("DeviceRGB");
        var photoId = new PdfReference(simpleDocument.Objects.Max(item => item.Id.ObjectNumber) + 1);
        simpleDocument.Objects.Add(new(photoId, photo));
        simpleGraph.Pages()[0].Resources.Entries!["XObject"] = Dict(("Photo", Ref(photoId)));
        var simpleBefore = PdfConverter.ToJson(simpleDocument);
        var imageOnlyRemoval = PdfContentRedactor.Apply(simpleDocument, [new(1, 300, 300, 20, 20)]);
        Check(PdfTextExtractor.Extract(imageOnlyRemoval)[0].Text.Contains("AB"), "image redaction retains simple embedded Arial-style text");
        Check(!imageOnlyRemoval.Objects.Any(item => item.Value.Entries?.GetValueOrDefault("Subtype")?.Name == "Image"), "selected image removed without removing unrelated text");
        Check(PdfConverter.ToJson(simpleDocument) == simpleBefore, "simple font source unchanged");
        // Code 32 maps to the A outline; Tw must still advance it after conversion to CID text.
        simpleFont.Entries!["Encoding"] = Dict(("BaseEncoding", Name("WinAnsiEncoding")), ("Differences", PdfObjectKit.Primitives.PdfPrimitives.Array(Num(32), Name("A"))));
        simpleFont.Entries["FirstChar"] = Num(32);
        simpleFont.Entries["Widths"] = PdfObjectKit.Primitives.PdfPrimitives.Array(Enumerable.Repeat(Num(600), 35).ToArray());
        simpleGraph.Pages()[0].Streams[0].Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("BT /F1 12 Tf 7 Tw 2 Tc 20 100 Td (A B) Tj ET"));
        var originalEnd = new PdfPageInterpreter(simpleGraph, simpleGraph.Pages()[0]).Slots.Last().BaselineEnd;
        var spacedResult = PdfContentRedactor.Apply(simpleDocument, [new(1, 300, 300, 20, 20)]);
        var spacedGraph = new PdfGraph(spacedResult);
        var rebuiltEnd = new PdfPageInterpreter(spacedGraph, spacedGraph.Pages()[0]).Slots.Last().BaselineEnd;
        Check(Math.Abs(originalEnd.X - rebuiltEnd.X) < .000001, "single-byte word and character spacing preserved after CID conversion");
        var year = Fixture("BT /F1 12 Tf 20 100 Td (N 2025) Tj ET");
        var yearRegion = new PdfContentRedaction(1, 45.344, 95, 14, 18);
        var yearPreview = PdfContentRedactor.Preview(year, [yearRegion]);
        Check(yearPreview.Count(item => item.Kind == "text") == 2, "selecting 25 highlights only two glyphs, not N 20");
        var yearResult = PdfContentRedactor.Apply(year, [yearRegion]);
        Check(PdfTextExtractor.Extract(yearResult)[0].Text.Trim() == "N 20", "selecting 25 preserves N 20");
        var scaledYear = Fixture("BT /F1 12 Tf 150 Tz 7 Ts 20 100 Td (N 2025) Tj ET");
        var scaledYearResult = PdfContentRedactor.Apply(scaledYear, [new(1, 58.016, 104, 21, 18)]);
        Check(PdfTextExtractor.Extract(scaledYearResult)[0].Text.Trim() == "N 20", "precise glyphs retain horizontal scale and text rise");
        var embeddedAdjacent = PdfReader.Read(PdfFixture.Create(fontBytes, new FixturePage("AB", X: 20, Y: 100)));
        var adjacentGraph = new PdfGraph(embeddedAdjacent);
        var adjacentSlot = new PdfPageInterpreter(adjacentGraph, adjacentGraph.Pages()[0]).Slots.Single();
        var nextGlyphX = 20 + adjacentSlot.Glyphs[0].Width * adjacentSlot.FontSize;
        var adjacentRegion = new PdfContentRedaction(1, nextGlyphX + adjacentSlot.FontSize * .1, 99, adjacentSlot.FontSize * .4, adjacentSlot.FontSize);
        var adjacentPreview = PdfContentRedactor.Preview(embeddedAdjacent, [adjacentRegion]);
        Check(adjacentPreview.Count(item => item.Kind == "text") == 1, "embedded glyph boxes do not select preceding glyph");
        Check(PdfTextExtractor.Extract(PdfContentRedactor.Apply(embeddedAdjacent, [adjacentRegion]))[0].Text.Trim() == "A", "embedded glyph selection removes B only");
        var stroked = Fixture("BT /F1 12 Tf 30 w 1 Tr 20 100 Td (STROKED) Tj ET");
        var strokeResult = PdfContentRedactor.Apply(stroked, [new(1, 20, 75, 30, 5)]);
        Check(!PdfTextExtractor.Extract(strokeResult)[0].Text.Contains("STROKED"), "stroke extent outside glyph metrics is included");
        var graphics = Fixture("q /Paint gs BT /F1 12 Tf 20 100 Td (SECRET) Tj 0 150 Td (PUBLIC) Tj ET Q");
        var graphicsGraph = new PdfGraph(graphics);
        graphicsGraph.Pages()[0].Resources.Entries!["ExtGState"] = Dict(("Paint", Dict(
            ("Type", Name("ExtGState")), ("ca", Num(.5)), ("CA", Num(1)),
            ("BM", Name("Normal")), ("SMask", Name("None")), ("LW", Num(30)), ("ML", Num(2)))));
        var graphicsBefore = PdfConverter.ToJson(graphics);
        var graphicsResult = PdfContentRedactor.Apply(graphics, [new(1, 0, 80, 150, 50)]);
        var graphicsText = PdfTextExtractor.Extract(graphicsResult)[0].Text;
        Check(!graphicsText.Contains("SECRET") && graphicsText.Contains("PUBLIC"), "ExtGState text redacted with public text retained");
        var cleanGraph = new PdfGraph(graphicsResult);
        var cleanState = cleanGraph.Get(cleanGraph.Pages()[0].Resources, "ExtGState")!.Entries!.Values.Single();
        Check(cleanGraph.Number(cleanState, "ca") == .5 && cleanGraph.Get(cleanState, "BM")!.Name == "Normal", "opacity and normal blending preserved");
        Check(PdfConverter.ToJson(graphics) == graphicsBefore, "graphics state source unchanged");
        using (var independent = NativeTestDocument.Open(PdfWriter.Write(graphicsResult)))
        {
            Check(independent.NumberOfPages == 1, "graphics state output independently readable");
        }
        var gsStroke = Fixture("/Wide gs BT /F1 12 Tf 1 Tr 20 100 Td (STROKED) Tj ET");
        new PdfGraph(gsStroke).Pages()[0].Resources.Entries!["ExtGState"] = Dict(("Wide", Dict(("LW", Num(30)), ("ML", Num(2)))));
        Check(!PdfTextExtractor.Extract(PdfContentRedactor.Apply(gsStroke, [new(1, 20, 30, 30, 5)]))[0].Text.Contains("STROKED"), "ExtGState stroke extent included in removal");
        var transparent = Fixture("q /Hidden gs BT /F1 12 Tf 20 100 Td (HIDDEN) Tj ET Q BT /F1 12 Tf 20 200 Td (PUBLIC) Tj ET");
        new PdfGraph(transparent).Pages()[0].Resources.Entries!["ExtGState"] = Dict(("Hidden", Dict(("ca", Num(0)))));
        var transparentText = PdfTextExtractor.Extract(PdfContentRedactor.Apply(transparent, [new(1, 500, 500, 10, 10)]))[0].Text;
        Check(!transparentText.Contains("HIDDEN") && transparentText.Contains("PUBLIC"), "zero alpha text removed and q/Q restores visibility");
        foreach (var unsafeState in new[]
        {
            Dict(("SMask", Dict())), Dict(("BM", Name("UnknownBlend"))), Dict(("ca", Num(2))),
            Dict(("Font", PdfObjectKit.Primitives.PdfPrimitives.Array(Name("F1"), Num(12)))),
            Dict(("Secret", Str(Encoding.ASCII.GetBytes("SECRET"))))
        })
        {
            var unsafeDocument = Fixture("/Unsafe gs");
            new PdfGraph(unsafeDocument).Pages()[0].Resources.Entries!["ExtGState"] = Dict(("Unsafe", unsafeState));
            var unsafeBefore = PdfConverter.ToJson(unsafeDocument);
            try
            {
                PdfContentRedactor.Apply(unsafeDocument, [new(1, 0, 0, 10, 10)]);
                throw new Exception("Unsafe ExtGState accepted");
            }
            catch (NotSupportedException exception)
            {
                Check(exception.Message.Contains("ExtGState /") && PdfConverter.ToJson(unsafeDocument) == unsafeBefore, "unsafe ExtGState rejected by entry without source mutation");
            }
        }
        var transformed = Fixture("2 0 0 2 100 100 cm BT /F1 12 Tf 20 100 Td (SECRET) Tj ET");
        var transformedResult = PdfContentRedactor.Apply(transformed, [new(1, 130, 280, 130, 50)]);
        Check(!PdfTextExtractor.Extract(transformedResult)[0].Text.Contains("SECRET"), "page transforms included in intersection");
        var shared = Fixture("q 1 0 0 1 0 0 cm /Shared Do Q q 1 0 0 1 200 0 cm /Shared Do Q");
        var sharedGraph = new PdfGraph(shared);
        var form = Stream(Encoding.ASCII.GetBytes("/FormPaint gs BT /F1 12 Tf 20 100 Td (SHAREDSECRET) Tj ET"));
        form.Entries!["Type"] = Name("XObject");
        form.Entries["Subtype"] = Name("Form");
        form.Entries["BBox"] = PdfObjectKit.Primitives.PdfPrimitives.Array(Num(0), Num(0), Num(200), Num(200));
        form.Entries["Resources"] = Dict(("Font", sharedGraph.Get(sharedGraph.Pages()[0].Resources, "Font")!), ("ExtGState", Dict(("FormPaint", Dict(("ca", Num(.7)))))));
        var id = new PdfReference(shared.Objects.Max(item => item.Id.ObjectNumber) + 1);
        shared.Objects.Add(new(id, form));
        // Separate resource dictionary prevents a self-referential test fixture.
        sharedGraph.Pages()[0].Dictionary.Entries!["Resources"] = Dict(("Font", sharedGraph.Get(sharedGraph.Pages()[0].Resources, "Font")!), ("XObject", Dict(("Shared", Ref(id)))));
        var sharedResult = PdfContentRedactor.Apply(shared, [new(1, 0, 80, 150, 50)]);
        Check(!PdfTextExtractor.Extract(sharedResult)[0].Text.Contains("SECRET"), "shared Form source glyphs removed at all placements");
        var forms = PdfReader.Read(PdfFixture.Create(null, new FixturePage("PUBLIC")));
        PdfFormEditor.InsertTextField(forms, 1, "SecretName", "SECRET", 20, 200, 100, 25);
        var formsResult = PdfContentRedactor.Apply(forms, [new(1, 10, 190, 120, 50)]);
        Check(PdfFormExtractor.Extract(formsResult).Count == 0 && !PdfTextExtractor.Extract(formsResult)[0].Text.Contains("SECRET"), "field appearances redacted and field values removed");
        var hidden = Fixture("BT /F1 12 Tf 20 100 Td (PUBLIC) Tj 3 Tr 0 300 Td (INVISIBLESECRET) Tj ET");
        var hiddenResult = PdfContentRedactor.Apply(hidden, [new(1, 500, 500, 10, 10)]);
        Check(!PdfTextExtractor.Extract(hiddenResult)[0].Text.Contains("INVISIBLE"), "invisible text removed even outside marked regions");
        var imageSource = Fixture("q 100 0 0 100 0 0 cm /SharedImage Do Q q 100 0 0 100 200 0 cm /SharedImage Do Q q 10 0 0 10 400 400 cm /Kept Do Q");
        var imageGraph = new PdfGraph(imageSource);
        var image = Stream(new byte[] { 255, 0, 0 });
        image.Entries!["Type"] = Name("XObject");
        image.Entries["Subtype"] = Name("Image");
        image.Entries["Width"] = Num(1);
        image.Entries["Height"] = Num(1);
        image.Entries["BitsPerComponent"] = Num(8);
        image.Entries["ColorSpace"] = Name("DeviceRGB");
        var imageId = new PdfReference(imageSource.Objects.Max(item => item.Id.ObjectNumber) + 1);
        imageSource.Objects.Add(new(imageId, image));
        var kept = Stream(new byte[] { 0, 255, 0 });
        kept.Entries = new(image.Entries);
        var keptId = new PdfReference(imageId.ObjectNumber + 1);
        imageSource.Objects.Add(new(keptId, kept));
        imageGraph.Pages()[0].Resources.Entries!["XObject"] = Dict(("SharedImage", Ref(imageId)), ("Kept", Ref(keptId)));
        var imageResult = PdfContentRedactor.Apply(imageSource, [new(1, 20, 20, 20, 20)]);
        var outputImages = imageResult.Objects.Where(item => item.Value.Entries?.GetValueOrDefault("Subtype")?.Name == "Image").ToArray();
        var imagePreviewBefore = PdfConverter.ToJson(imageSource);
        var imageImpacts = PdfContentRedactor.Preview(imageSource, [new(1, 20, 20, 20, 20)]);
        Check(imageImpacts.Count(item => item.Kind == "image") == 2 && imageImpacts.Any(item => item.X == 200 && item.Width == 100), "preview reports shared image removal outside selected rectangle");
        Check(PdfConverter.ToJson(imageSource) == imagePreviewBefore, "impact preview leaves source unchanged");
        var textImpacts = PdfContentRedactor.Preview(shared, [new(1, 0, 80, 150, 50)]);
        Check(textImpacts.Any(item => item.Kind == "text" && item.X > 190), "preview includes other Form occurrences");
        Check(outputImages.Length == 1 && PdfStreamDecoder.Decode(outputImages[0].Value).SequenceEqual(new byte[] { 0, 255, 0 }), "shared intersecting image removed everywhere, unrelated pixels preserved");
        foreach (var content in new[] { "0 0 m 100 0 l 100 100 l W n", "/Unknown gs", "(SECRET) 0 g", "BT /F1 12 Tf 4 Tr (SECRET) Tj ET" })
        {
            var invalid = Fixture(content);
            var original = PdfConverter.ToJson(invalid);
            try
            {
                PdfContentRedactor.Apply(invalid, [new(1, 0, 0, 100, 100)]);
                throw new Exception("Unsupported content accepted");
            }
            catch (NotSupportedException)
            {
                Check(PdfConverter.ToJson(invalid) == original, "unsupported content rejected atomically");
            }
        }
        var colored = Fixture("/RGB cs 1 0 0 scn q /Paint gs BT /F1 12 Tf 20 100 Td (PUBLIC) Tj ET Q 0 g 0 sc");
        var coloredGraph = new PdfGraph(colored);
        coloredGraph.Pages()[0].Resources.Entries!["ColorSpace"] = Dict(("RGB", Name("DeviceRGB")));
        coloredGraph.Pages()[0].Resources.Entries!["ExtGState"] = Dict(("Paint", Dict(("BM", Name("Multiply")), ("FL", Num(1)),
            ("RI", Name("Perceptual")), ("D", PdfObjectKit.Primitives.PdfPrimitives.Array(PdfObjectKit.Primitives.PdfPrimitives.Array(Num(2), Num(3)), Num(1))))));
        var coloredResult = PdfContentRedactor.Apply(colored, [new(1, 400, 400, 10, 10)]);
        var coloredOutput = new PdfGraph(coloredResult);
        var coloredOps = Encoding.ASCII.GetString(PdfStreamDecoder.Decode(coloredOutput.Pages()[0].Streams[0].Value));
        Check(coloredOps.Contains("/DeviceRGB cs") && coloredOps.Contains("scn"), "device color resource aliases normalized and color retained");
        Check(coloredOutput.Get(coloredOutput.Pages()[0].Resources, "ExtGState")!.Entries!.Values.Any(value => coloredOutput.Get(value, "BM")?.Name == "Multiply"), "standard blend mode retained");
        form.Entries!["Group"] = Dict(("S", Name("Transparency")), ("CS", Name("DeviceRGB")), ("I", new PdfValue { Type = "boolean", Boolean = true }));
        var groupedResult = PdfContentRedactor.Apply(shared, [new(1, 0, 80, 150, 50)]);
        Check(!PdfTextExtractor.Extract(groupedResult)[0].Text.Contains("SECRET") && groupedResult.Objects.Any(item => item.Value.Entries?.ContainsKey("Group") == true), "transparency Form redaction preserves group semantics");
        var mapped = Fixture("BT /F1 12 Tf 20 100 Td (A) Tj 0 150 Td (B) Tj ET");
        var mappedGraph = new PdfGraph(mapped);
        var mappedFont = mappedGraph.Resolve(mappedGraph.Get(mappedGraph.Pages()[0].Resources, "Font")!.Entries!["F1"]);
        var secretUnicode = Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("SECRET"));
        var publicUnicode = Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("PUBLIC"));
        var mapId = new PdfReference(mapped.Objects.Max(item => item.Id.ObjectNumber) + 1);
        mapped.Objects.Add(new(mapId, Stream(Encoding.ASCII.GetBytes($"1 begincodespacerange <00> <FF> endcodespacerange 2 beginbfchar <41> <{secretUnicode}> <42> <{publicUnicode}> endbfchar"))));
        mappedFont.Entries!["ToUnicode"] = Ref(mapId);
        var mappedResult = PdfContentRedactor.Apply(mapped, [new(1, 0, 80, 50, 50)]);
        Check(PdfTextExtractor.Extract(mappedResult)[0].Text.Contains("PUBLIC") && !PdfTextExtractor.Extract(mappedResult)[0].Text.Contains("SECRET"), "standard font custom Unicode mapping retained selectively");
        Check(!Encoding.ASCII.GetString(PdfWriter.Write(mappedResult)).Contains(secretUnicode), "removed ligature mapping is absent from saved bytes");
        var maskedDocument = Fixture("q 20 0 0 20 0 0 cm /Photo Do Q q 20 0 0 20 100 0 cm /Photo Do Q");
        var maskedGraph = new PdfGraph(maskedDocument);
        var maskSamples = Stream(new byte[] { 0, 255 });
        maskSamples.Entries!["Width"] = Num(2);
        maskSamples.Entries["Height"] = Num(1);
        maskSamples.Entries["BitsPerComponent"] = Num(8);
        maskSamples.Entries["ColorSpace"] = Name("DeviceGray");
        maskSamples.Entries["Decode"] = PdfObjectKit.Primitives.PdfPrimitives.Array(Num(1), Num(0));
        maskSamples.Entries["Matte"] = PdfObjectKit.Primitives.PdfPrimitives.Array(Num(1), Num(1), Num(1));
        var maskedImage = Stream(new byte[] { 100, 150, 200, 20, 30, 40 });
        maskedImage.Entries!["Subtype"] = Name("Image");
        maskedImage.Entries["Width"] = Num(2);
        maskedImage.Entries["Height"] = Num(1);
        maskedImage.Entries["BitsPerComponent"] = Num(8);
        maskedImage.Entries["ColorSpace"] = Name("DeviceRGB");
        maskedImage.Entries["Decode"] = PdfObjectKit.Primitives.PdfPrimitives.Array(Num(1), Num(0), Num(0), Num(1), Num(0), Num(1));
        var maskId = new PdfReference(maskedDocument.Objects.Max(item => item.Id.ObjectNumber) + 1);
        maskedDocument.Objects.Add(new(maskId, maskSamples));
        var maskedImageId = new PdfReference(maskId.ObjectNumber + 1);
        maskedImage.Entries["SMask"] = Ref(maskId);
        maskedDocument.Objects.Add(new(maskedImageId, maskedImage));
        maskedGraph.Pages()[0].Resources.Entries!["XObject"] = Dict(("Photo", Ref(maskedImageId)));
        var maskedResult = PdfContentRedactor.Apply(maskedDocument, [new(1, 400, 400, 10, 10)]);
        var masksOutput = new PdfGraph(maskedResult);
        var cleanMasked = maskedResult.Objects.Single(item => item.Value.Entries?.ContainsKey("SMask") == true).Value;
        Check(PdfStreamDecoder.Decode(cleanMasked).SequenceEqual(new byte[] { 155, 150, 200, 235, 30, 40 }), "inverted image Decode samples normalized");
        var cleanAlpha = masksOutput.Get(cleanMasked, "SMask")!;
        Check(PdfStreamDecoder.Decode(cleanAlpha).SequenceEqual(new byte[] { 255, 0 }) && masksOutput.Get(cleanAlpha, "Matte")!.Items!.Count == 3, "soft mask and matte rebuilt with normalized alpha");
        Check(maskedResult.Objects.Count(item => item.Value.Entries?.GetValueOrDefault("ColorSpace")?.Name == "DeviceRGB") == 1, "shared retained image stored only once");
        var removedMasked = PdfContentRedactor.Apply(maskedDocument, [new(1, 0, 0, 20, 20)]);
        Check(!removedMasked.Objects.Any(item => item.Value.Entries?.GetValueOrDefault("Subtype")?.Name == "Image"), "selected shared image and its mask both removed");
        maskSamples.Base64 = Convert.ToBase64String(new byte[] { 0, 255, 42 });
        var malformedMaskBefore = PdfConverter.ToJson(maskedDocument);
        try
        {
            PdfContentRedactor.Apply(maskedDocument, [new(1, 400, 400, 10, 10)]);
            throw new Exception("Trailing mask payload accepted");
        }
        catch (NotSupportedException exception)
        {
            Check(exception.Message.Contains("sample length") && PdfConverter.ToJson(maskedDocument) == malformedMaskBefore, "trailing mask data rejected atomically");
        }
        maskSamples.Base64 = Convert.ToBase64String(new byte[] { 0, 255 });
        foreach (var invalidContent in new[] { "/DeviceRGB cs 1 scn", "/Pattern cs /P scn", "/Unknown gs" })
        {
            var invalidColor = Fixture(invalidContent);
            try
            {
                PdfContentRedactor.Apply(invalidColor, [new(1, 400, 400, 10, 10)]);
                throw new Exception("Invalid color accepted");
            }
            catch (NotSupportedException)
            {
                Check(true, "invalid color and unresolved state rejected");
            }
        }
        maskedImage.Entries.Remove("SMask");
        maskedImage.Entries.Remove("Decode");
        maskedImage.Entries["ColorSpace"] = Name("DeviceCMYK");
        maskedImage.Entries["Width"] = Num(1);
        maskedImage.Base64 = Convert.ToBase64String(new byte[] { 0, 255, 255, 0 });
        var cmykResult = PdfContentRedactor.Apply(maskedDocument, [new(1, 400, 400, 10, 10)]);
        Check(cmykResult.Objects.Any(item => item.Value.Entries?.GetValueOrDefault("ColorSpace")?.Name == "DeviceCMYK" && PdfStreamDecoder.Decode(item.Value).SequenceEqual(new byte[] { 0, 255, 255, 0 })), "CMYK image samples preserved");
        Console.WriteLine("Content redaction text, advance, paths, shared Forms, field appearances, sanitization and rejection checks passed.");
    }
}
