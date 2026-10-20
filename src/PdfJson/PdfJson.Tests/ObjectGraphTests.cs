using System.Text;
using Practical.PdfJson;

internal static class ObjectGraphTests
{
    public static void Run(byte[] simplePdf)
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("FAILED object graph: " + label);
            checks++;
        }
        void Reject(Action action, string label)
        {
            try { action(); }
            catch (InvalidDataException) { checks++; return; }
            throw new Exception("FAILED object graph rejection: " + label);
        }
        var simple = PdfObjectConverter.ReadPdf(simplePdf);
        Check(simple.TextIndex.Count == 3, "text index includes blank pages");
        Check(simple.TextIndex[0].Text.Contains("Hello world"), "readable page text");
        Check(simple.TextIndex[2].Text == "" && simple.TextIndex[2].Warning is null, "blank page text");
        var simpleRebuilt = PdfObjectConverter.WritePdf(PdfObjectConverter.FromJson(PdfObjectConverter.ToJson(simple)));
        using (var parsed = NativeTestDocument.Open(simpleRebuilt))
        {
            Check(parsed.NumberOfPages == 3, "page count");
            Check(parsed.GetPage(1).Text.Contains("Hello world"), "original content stream");
        }

        var graph = PdfObjectConverter.ReadPdf(CreateFixture());
        Check(simple.ImageIndex.Count == 3 && simple.ImageIndex.All(p => p.Images.Count == 0 && p.Warning is null), "image index includes empty pages");
        Check(graph.ImageIndex.Count == 1 && graph.ImageIndex[0].Warning is null, "image index page");
        var indexedImage = graph.ImageIndex[0].Images.Single();
        Check(indexedImage.PixelWidth == 1 && indexedImage.PixelHeight == 1 && indexedImage.BitsPerComponent == 8, "image pixel metadata");
        Check(indexedImage.ImageObjects.SequenceEqual([new PdfReference(5)]), "image object reference");
        Check(indexedImage.Corners.SequenceEqual([new PdfImagePoint(20, 260), new PdfImagePoint(60, 260), new PdfImagePoint(60, 300), new PdfImagePoint(20, 300)]), "image placement corners");
        Check(!indexedImage.IsInline && indexedImage.InlineDataBase64 is null, "XObject payload not duplicated");
        var manyImages = PdfObjectConverter.ReadPdf(CreateFixture(extraImages: true));
        Check(manyImages.ImageIndex[0].Warning is null && manyImages.ImageIndex[0].Images.Count == 3, "repeated and inline image occurrences");
        var resourceImages = manyImages.ImageIndex[0].Images.Where(i => !i.IsInline).ToList();
        Check(resourceImages.Count == 2 && resourceImages.All(i => i.ImageObjects.SequenceEqual([new PdfReference(5)])), "shared image resource occurrence mapping");
        Check(resourceImages[0].Corners[0] != resourceImages[1].Corners[0], "shared image has distinct placements");
        var inlineImage = manyImages.ImageIndex[0].Images.Single(i => i.IsInline);
        Check(inlineImage.ImageObjects.Count == 0 && Convert.FromBase64String(inlineImage.InlineDataBase64!).SequenceEqual(new byte[] { 255, 0, 0 }), "inline image preserves fixture payload");
        var unchangedImagePdf = PdfObjectConverter.WritePdf(manyImages);
        manyImages.ImageIndex[0].Images.Clear();
        Check(unchangedImagePdf.SequenceEqual(PdfObjectConverter.WritePdf(manyImages)), "image index informational only");
        var json = PdfObjectConverter.ToJson(graph);
        Check(graph.TextIndex[0].ContentStreams.SequenceEqual([new PdfReference(10)]), "text index content reference");
        Check(graph.TextIndex[0].Text.Contains("Object graph"), "compressed text index");
        Check(graph.TextIndex[0].FormStreams.Count == 0, "image excluded from form candidates");
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        legacyJson.AsObject().Remove("textIndex");
        legacyJson.AsObject().Remove("imageIndex");
        Check(PdfObjectConverter.FromJson(legacyJson.ToJsonString()).ImageIndex.Count == 0, "old JSON without imageIndex supported");
        Check(PdfObjectConverter.FromJson(legacyJson.ToJsonString()).TextIndex.Count == 0, "JSON without index remains supported");
        var indexed = PdfObjectConverter.FromJson(json);
        Check(indexed.ImageIndex[0].Images[0].EncodedDataSha256 == indexedImage.EncodedDataSha256, "image index JSON round trip");
        Check(indexed.TextIndex[0].Text == graph.TextIndex[0].Text, "text index JSON round trip");
        var beforeIndexEdit = PdfObjectConverter.WritePdf(indexed);
        indexed.TextIndex[0].Text = indexed.TextIndex[0].Text.Replace("Object graph", "Object");
        var editedBytes = PdfObjectConverter.WritePdf(indexed);
        Check(!beforeIndexEdit.SequenceEqual(editedBytes), "index edits change PDF bytes");
        Check(PdfObjectConverter.ReadPdf(editedBytes).TextIndex[0].Text == indexed.TextIndex[0].Text, "index edit updates extracted text");
        Check(editedBytes.SequenceEqual(PdfObjectConverter.WritePdf(indexed)), "repeated write does not mutate source graph");
        indexed.TextIndex[0].Text = "This replacement is much wider than the original content";
        using (var wider = NativeTestDocument.Open(PdfObjectConverter.WritePdf(indexed)))
        {
            Check(wider.GetPage(1).Text == indexed.TextIndex[0].Text, "wider replacement allowed by default");
            Check(wider.GetPage(1).Letters.All(l => Math.Abs(l.FontSize - 18) < 0.001), "wider replacement retains font size");
        }
        indexed.TextIndex[0].Text = "\u4e2d";
        Reject(() => PdfObjectConverter.WritePdf(indexed), "unavailable glyph");
        var positionedEdit = PdfObjectConverter.ReadPdf(CreateFixture(positioned: true));
        Check(positionedEdit.TextIndex[0].Runs!.Count == 3, "individual Tj and TJ operands exported");
        var runDocument = PdfObjectConverter.FromJson(PdfObjectConverter.ToJson(positionedEdit));
        var runs = runDocument.TextIndex[0].Runs!;
        runs.Single(r => r.Text == "Object ").Text = "Item ";
        runs.Single(r => r.Text == "graph").Text = "map";
        runs.Single(r => r.Text == " tail").Text = " a";
        var editedRuns = PdfObjectConverter.WritePdf(runDocument);
        using (var runPdf = NativeTestDocument.Open(editedRuns))
            Check(runPdf.GetPage(1).Text == "Item map a", "multiple edits within and across operators");
        Check(PdfObjectConverter.ReadPdf(editedRuns).TextIndex[0].Runs!.Any(r => r.Text == "map"), "re-export refreshes run mappings");
        Check(PdfObjectConverter.WritePdf(runDocument).SequenceEqual(editedRuns), "run writes are repeatable");
        runs[0].StreamSha256 = "stale";
        Reject(() => PdfObjectConverter.WritePdf(runDocument), "stale run hash rejected");
        runDocument = PdfObjectConverter.FromJson(PdfObjectConverter.ToJson(positionedEdit));
        runDocument.TextIndex[0].Runs![0].OriginalText = "wrong";
        Reject(() => PdfObjectConverter.WritePdf(runDocument), "stale original run text rejected");
        runDocument = PdfObjectConverter.FromJson(PdfObjectConverter.ToJson(positionedEdit));
        runDocument.TextIndex[0].Runs!.Add(runDocument.TextIndex[0].Runs![0]);
        Reject(() => PdfObjectConverter.WritePdf(runDocument), "duplicate run rejected");
        runDocument = PdfObjectConverter.FromJson(PdfObjectConverter.ToJson(positionedEdit));
        runDocument.TextIndex[0].Runs![0].Text = "Item ";
        runDocument.TextIndex[0].Text = "Changed summary";
        Reject(() => PdfObjectConverter.WritePdf(runDocument), "mixed run and summary edits rejected");
        var duplicateWords = PdfObjectConverter.ReadPdf(CreateFixture(repeated: true));
        var repeatedRuns = duplicateWords.TextIndex[0].Runs!;
        Check(repeatedRuns.Count(r => r.Text == "Repeat") == 2, "identical text has separate run identities");
        repeatedRuns[1].Text = "Again";
        using (var repeatedPdf = NativeTestDocument.Open(PdfObjectConverter.WritePdf(duplicateWords)))
            Check(repeatedPdf.GetPage(1).Text == "RepeatAgain", "only selected identical occurrence changes");
        runDocument = PdfObjectConverter.FromJson(PdfObjectConverter.ToJson(positionedEdit));
        runDocument.TextIndex[0].Runs![0].Text = "A much longer first string ";
        runDocument.TextIndex[0].Runs![1].Text = "a much longer second string";
        using (var overflowRuns = NativeTestDocument.Open(PdfObjectConverter.WritePdf(runDocument)))
        using (var originalRuns = NativeTestDocument.Open(CreateFixture(positioned: true)))
        {
            Check(overflowRuns.GetPage(1).Text == "A much longer first string a much longer second string tail", "overlapping run edits allowed");
            Check(overflowRuns.GetPage(1).Letters.All(l => Math.Abs(l.FontSize - 18) < 0.001), "overlapping runs retain font size");
            Check(Math.Abs(overflowRuns.GetPage(1).Letters.Last().StartBaseLine.X - originalRuns.GetPage(1).Letters.Last().StartBaseLine.X) < 0.001, "overflow leaves following text in place");
        }
        runDocument = PdfObjectConverter.FromJson(PdfObjectConverter.ToJson(positionedEdit));
        runDocument.TextIndex[0].Runs![1].Text = "";
        using (var deletedRun = NativeTestDocument.Open(PdfObjectConverter.WritePdf(runDocument)))
            Check(deletedRun.GetPage(1).Text == "Object  tail", "empty run text removes operand text");
        using var beforePositioned = NativeTestDocument.Open(CreateFixture(positioned: true));
        var oldTail = beforePositioned.GetPage(1).Letters.Last().StartBaseLine;
        positionedEdit.TextIndex[0].Text = positionedEdit.TextIndex[0].Text.Replace("graph", "gap");
        using (var afterPositioned = NativeTestDocument.Open(PdfObjectConverter.WritePdf(positionedEdit)))
        {
            Check(afterPositioned.GetPage(1).Text.Contains("gap"), "TJ text replacement");
            Check(Math.Abs(afterPositioned.GetPage(1).Letters.Last().StartBaseLine.X - oldTail.X) < 0.001, "following text position preserved");
        }
        var shared = PdfObjectConverter.FromJson(json);
        shared.Objects.Single(o => o.Id.ObjectNumber == 1).Value.Entries!["Shared"] = new() { Type = "reference", Reference = new(10) };
        shared.TextIndex[0].Text = shared.TextIndex[0].Text.Replace("graph", "gap");
        Reject(() => PdfObjectConverter.WritePdf(shared), "shared stream edit");
        var forms = PdfObjectConverter.ReadPdf(CreateFixture(withForms: true));
        Check(forms.ImageIndex[0].Warning is null && forms.ImageIndex[0].Images.Count == 2, "nested form image indexed");
        var rotatedImage = forms.ImageIndex[0].Images[1];
        Check(rotatedImage.Corners[0] == new PdfImagePoint(100, 100) && rotatedImage.Corners[1] == new PdfImagePoint(100, 120), "rotated image corners preserved");
        Check(forms.TextIndex[0].Warning is null, "form indexing successful");
        Check(forms.TextIndex[0].ContentStreams.SequenceEqual([new PdfReference(10), new PdfReference(11)]), "content array references");
        Check(forms.TextIndex[0].FormStreams.SequenceEqual([new PdfReference(12), new PdfReference(13)]), "inherited nested form resources");
        Check(forms.TextIndex[0].Text.Contains("Nested form"), "nested form readable text");
        forms.TextIndex[0].Text = forms.TextIndex[0].Text.Replace("Nested form", "Form");
        Reject(() => PdfObjectConverter.WritePdf(forms), "form edit explicitly rejected");
        var rebuilt = PdfObjectConverter.WritePdf(PdfObjectConverter.FromJson(json));
        var again = PdfObjectConverter.ReadPdf(rebuilt);
        using (var independent = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(rebuilt), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        {
            Check(independent.PageCount == 1, "independent PDFsharp reader");
            Check(independent.AcroForm.Fields.Count == 1, "independent form field tree");
        }
        Check(graph.Objects.Count == again.Objects.Count, "object count");
        foreach (var obj in graph.Objects)
        {
            var other = again.Objects.Single(o => o.Id == obj.Id);
            // Re-exporting should yield the same canonical graph, including binary bytes.
            Check(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.JsonSerializer.SerializeToNode(obj.Value),
                System.Text.Json.JsonSerializer.SerializeToNode(other.Value)), $"object {obj.Id}");
        }
        Check(json.Contains("AQIDAP8="), "embedded binary encoded as Base64");
        Check(!json.Contains("originalPdfBase64"), "no whole-file payload");
        using (var parsed = NativeTestDocument.Open(rebuilt))
        {
            Check(parsed.GetPage(1).NumberOfImages == 1, "image XObject");
            Check(parsed.GetPage(1).Text.Contains("Object graph"), "text and font resources");
        }
        var info = graph.Objects.Single(o => o.Id.ObjectNumber == 9);
        info.Value.Entries!["Title"] = new() { Type = "string", Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("Edited metadata")) };
        using (var edited = NativeTestDocument.Open(PdfObjectConverter.WritePdf(graph)))
            Check(edited.Information.Title == "Edited metadata", "metadata JSON edits");
        graph.Objects.Add(graph.Objects[0]);
        Reject(() => PdfObjectConverter.WritePdf(graph), "duplicate object");
        graph.Objects.RemoveAt(graph.Objects.Count - 1);
        graph.Objects.RemoveAll(o => o.Id.ObjectNumber == 2);
        Reject(() => PdfObjectConverter.WritePdf(graph), "dangling reference");
        Reject(() => PdfObjectConverter.FromJson("{\"format\":\"wrong\",\"trailer\":{\"type\":\"null\"}}"), "format");
        var compressed = PdfObjectConverter.ReadPdf(CreateCompressedFixture());
        Check(compressed.Objects.Count == 3, "object streams expanded, storage objects removed");
        using (var expanded = NativeTestDocument.Open(PdfObjectConverter.WritePdf(compressed)))
            Check(expanded.NumberOfPages == 1, "xref stream and compressed objects round trip");
        var incremental = PdfObjectConverter.ReadPdf(AddIncrementalUpdate(CreateFixture()));
        using (var updated = NativeTestDocument.Open(PdfObjectConverter.WritePdf(incremental)))
            Check(updated.Information.Title == "Latest revision", "latest incremental revision wins");
        Check(incremental.Trailer.Entries!.ContainsKey("Custom"), "trailer extension inherited");
        var streamObject = incremental.Objects.Single(o => o.Id.ObjectNumber == 10);
        Check(streamObject.Value.Entries!.ContainsKey("Filter"), "compressed content filter preserved");
        using (var independent = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(PdfObjectConverter.WritePdf(compressed)), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
            Check(independent.PageCount == 1, "independent reader after object stream expansion");
        Console.WriteLine($"Passed {checks} object graph checks.");
    }

    // Independent PDF fixture: shared references, page-parent cycle, nonzero generation,
    // form widget, image, vector operators, attachment, metadata, name escapes and binary strings.
    private static byte[] CreateFixture(bool withForms = false, bool positioned = false, bool repeated = false, bool extraImages = false)
    {
        using var file = new MemoryStream();
        void Write(string s) => file.Write(Encoding.Latin1.GetBytes(s));
        var offsets = new List<(int Number, int Generation, long Offset)>();
        void Obj(int n, string body, int gen = 0)
        {
            offsets.Add((n, gen, file.Position)); Write($"{n} {gen} obj\n{body}\nendobj\n");
        }
        string Stream(string dictionary, byte[] bytes) => $"<< {dictionary} /Length {bytes.Length} >>\nstream\n" + Encoding.Latin1.GetString(bytes) + "\nendstream";
        Write("%PDF-1.7\n%âãÏÓ\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> /Names << /EmbeddedFiles << /Names [(payload.bin) 7 0 R] >> >> >>");
        var resources = " /Resources << /Font << /F1 4 0 R >> /XObject << /Im1 5 0 R " + (withForms ? "/Fm1 12 0 R" : "") + " >> >> ";
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 " + (withForms ? resources : "") + ">>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] " + (withForms ? "" : resources) + " /Contents " + (withForms ? "[10 0 R 11 0 R]" : "10 0 R") + " /Annots [6 0 R] >>");
        Obj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Obj(5, Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8", [255, 0, 0]));
        Obj(6, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /V (Alice) /Rect [20 200 180 220] /P 3 0 R /F 4 >>");
        Obj(7, "<< /Type /Filespec /F (payload.bin) /EF << /F 8 2 R >> >>");
        Obj(8, Stream("/Type /EmbeddedFile", [1, 2, 3, 0, 255]), 2);
        Obj(9, "<< /Title (Original title) /Binary <00FFFE8041> /Escaped#20Name /A#23B /Values [true false null -0.25 123] >>");
        var textOperators = repeated ? "(Repeat) Tj (Repeat) Tj" : positioned ? "[(Object ) 0 (graph)] TJ ( tail) Tj" : "(Object graph) Tj";
        var extraImageOperators = extraImages ? "\nq 20 0 0 20 100 260 cm /Im1 Do Q\nq 10 0 0 10 140 260 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID " + Encoding.Latin1.GetString([255, 0, 0]) + " EI Q" : "";
        Obj(10, Stream("/Filter /FlateDecode", Compress(Encoding.Latin1.GetBytes("BT /F1 18 Tf 20 350 Td " + textOperators + " ET\nq 40 0 0 40 20 260 cm /Im1 Do Q\n0 0 1 RG 20 240 m 180 240 l S" + extraImageOperators))));
        var xref = file.Position;
        if (withForms)
        {
            Obj(11, Stream("", Encoding.ASCII.GetBytes("q /Fm1 Do Q")));
            Obj(12, Stream("/Type /XObject /Subtype /Form /BBox [0 0 400 400] /Resources << /XObject << /Nested 13 0 R >> >>", Encoding.ASCII.GetBytes("q /Nested Do Q")));
            Obj(13, Stream("/Type /XObject /Subtype /Form /BBox [0 0 400 400] /Resources << /Font << /F1 4 0 R >> /XObject << /Im1 5 0 R >> >>", Encoding.ASCII.GetBytes("BT /F1 12 Tf 20 150 Td (Nested form) Tj ET q 0 20 -30 0 100 100 cm /Im1 Do Q")));
            xref = file.Position;
        }
        Write("xref\n0 1\n0000000000 65535 f \n");
        foreach (var (n, gen, offset) in offsets) Write($"{n} 1\n{offset:D10} {gen:D5} n \n");
        Write($"trailer\n<< /Size {(withForms ? 14 : 11)} /Root 1 0 R /Info 9 0 R /ID [<010203> <040506>] /Custom (trailer extension) >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zip = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal, true)) zip.Write(data);
        return output.ToArray();
    }

    private static byte[] AddIncrementalUpdate(byte[] original)
    {
        var text = Encoding.Latin1.GetString(original);
        var previous = text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..].Trim().Split('\n')[0].Trim();
        using var output = new MemoryStream();
        output.Write(original);
        void Write(string s) => output.Write(Encoding.ASCII.GetBytes(s));
        var offset = output.Position;
        Write("9 0 obj\n<< /Title (Latest revision) >>\nendobj\n");
        var xref = output.Position;
        Write($"xref\n9 1\n{offset:D10} 00000 n \ntrailer\n<< /Size 11 /Root 1 0 R /Info 9 0 R /Prev {previous} >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] CreateCompressedFixture()
    {
        var bodies = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> >>" };
        var header = $"1 0 2 {bodies[0].Length + 1} 3 {bodies[0].Length + bodies[1].Length + 2} ";
        var payload = Compress(Encoding.ASCII.GetBytes(header + string.Join(" ", bodies)));
        using var output = new MemoryStream();
        void Write(string s) => output.Write(Encoding.ASCII.GetBytes(s));
        Write("%PDF-1.7\n");
        var objOffset = output.Position;
        Write($"4 0 obj\n<< /Type /ObjStm /N 3 /First {header.Length} /Filter /FlateDecode /Length {payload.Length} >>\nstream\n");
        output.Write(payload); Write("\nendstream\nendobj\n");
        var xrefOffset = output.Position;
        using var entries = new MemoryStream();
        void Entry(byte type, long field, int generation)
        {
            entries.WriteByte(type);
            for (var i = 3; i >= 0; i--) entries.WriteByte((byte)(field >> (i * 8)));
            entries.WriteByte((byte)(generation >> 8)); entries.WriteByte((byte)generation);
        }
        Entry(0, 0, 65535);
        for (var i = 0; i < 3; i++) Entry(2, 4, i);
        Entry(1, objOffset, 0); Entry(1, xrefOffset, 0);
        var xref = Compress(entries.ToArray());
        Write($"5 0 obj\n<< /Type /XRef /Size 6 /Root 1 0 R /W [1 4 2] /Filter /FlateDecode /Length {xref.Length} >>\nstream\n");
        output.Write(xref); Write($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }
}
