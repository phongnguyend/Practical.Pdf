using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using PdfObjectKit.Converters;
using PdfObjectKit.Models;
using System.Text;
using PdfObjectKit;
using PdfObjectKit.Editors;

internal static class ObjectGraphTests
{
    public static void Run(byte[] simplePdf)
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition)
            {
                throw new Exception("FAILED object graph: " + label);
            }

            checks++;
        }

        void Reject(Action action, string label)
        {
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                checks++;
                return;
            }

            throw new Exception("FAILED object graph rejection: " + label);
        }

        var simple = PdfReader.Read(simplePdf);
        Check(simple.TextIndex.Count == 3, "text index includes blank pages");
        Check(simple.TextIndex[0].Text.Contains("Hello world"), "readable page text");
        Check(simple.TextIndex[2].Text == "" && simple.TextIndex[2].Warning is null, "blank page text");
        var simpleRebuilt = PdfWriter.Write(PdfConverter.FromJson(PdfConverter.ToJson(simple)));
        using (var parsed = NativeTestDocument.Open(simpleRebuilt))
        {
            Check(parsed.NumberOfPages == 3, "page count");
            Check(parsed.GetPage(1).Text.Contains("Hello world"), "original content stream");
        }

        var graph = PdfReader.Read(CreateFixture());
        Check(simple.ImageIndex.Count == 3 && simple.ImageIndex.All(p => p.Images.Count == 0 && p.Warning is null), "image index includes empty pages");
        Check(graph.ImageIndex.Count == 1 && graph.ImageIndex[0].Warning is null, "image index page");
        var indexedImage = graph.ImageIndex[0].Images.Single();
        Check(indexedImage.PixelWidth == 1 && indexedImage.PixelHeight == 1 && indexedImage.BitsPerComponent == 8, "image pixel metadata");
        Check(indexedImage.ImageObjects.SequenceEqual([new PdfReference(5)]), "image object reference");
        Check(indexedImage.Corners.SequenceEqual([new PdfImagePoint(20, 260), new PdfImagePoint(60, 260), new PdfImagePoint(60, 300), new PdfImagePoint(20, 300)]), "image placement corners");
        Check(!indexedImage.IsInline && indexedImage.InlineDataBase64 is null, "XObject payload not duplicated");
        var manyImages = PdfReader.Read(CreateFixture(extraImages: true));
        Check(manyImages.ImageIndex[0].Warning is null && manyImages.ImageIndex[0].Images.Count == 3, "repeated and inline image occurrences");
        var resourceImages = manyImages.ImageIndex[0].Images.Where(i => !i.IsInline).ToList();
        Check(resourceImages.Count == 2 && resourceImages.All(i => i.ImageObjects.SequenceEqual([new PdfReference(5)])), "shared image resource occurrence mapping");
        Check(resourceImages[0].Corners[0] != resourceImages[1].Corners[0], "shared image has distinct placements");
        var inlineImage = manyImages.ImageIndex[0].Images.Single(i => i.IsInline);
        Check(inlineImage.ImageObjects.Count == 0 && Convert.FromBase64String(inlineImage.InlineDataBase64!).SequenceEqual(new byte[] { 255, 0, 0 }), "inline image preserves fixture payload");
        var unchangedImagePdf = PdfWriter.Write(manyImages);
        manyImages.ImageIndex[0].Images.Clear();
        Check(unchangedImagePdf.SequenceEqual(PdfWriter.Write(manyImages)), "image index informational only");
        var json = PdfConverter.ToJson(graph);
        Check(graph.TextIndex[0].ContentStreams.SequenceEqual([new PdfReference(10)]), "text index content reference");
        Check(graph.TextIndex[0].Text.Contains("Object graph"), "compressed text index");
        Check(graph.TextIndex[0].FormStreams.Count == 0, "image excluded from form candidates");
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        legacyJson.AsObject().Remove("textIndex");
        legacyJson.AsObject().Remove("imageIndex");
        Check(PdfConverter.FromJson(legacyJson.ToJsonString()).ImageIndex.Count == 0, "old JSON without imageIndex supported");
        Check(PdfConverter.FromJson(legacyJson.ToJsonString()).TextIndex.Count == 0, "JSON without index remains supported");
        var indexed = PdfConverter.FromJson(json);
        Check(indexed.ImageIndex[0].Images[0].EncodedDataSha256 == indexedImage.EncodedDataSha256, "image index JSON round trip");
        Check(indexed.TextIndex[0].Text == graph.TextIndex[0].Text, "text index JSON round trip");
        var beforeIndexEdit = PdfWriter.Write(indexed);
        indexed.TextIndex[0].Text = "Changed summary";
        indexed.TextIndex[0].Runs![0].Text = "Changed run";
        indexed.TextIndex[0].Runs![0].StreamSha256 = "stale";
        var editedBytes = PdfWriter.Write(PdfConverter.FromJson(PdfConverter.ToJson(indexed)));
        Check(beforeIndexEdit.SequenceEqual(editedBytes), "JSON summary/run/checksum changes do not affect PDF bytes");
        Check(PdfReader.Read(editedBytes).TextIndex[0].Text == "Object graph", "JSON text changes ignored");
        indexed.TextIndex.Clear();
        Check(beforeIndexEdit.SequenceEqual(PdfWriter.Write(indexed)), "removing text index does not affect PDF bytes");
        var positionedEdit = PdfReader.Read(CreateFixture(positioned: true));
        Check(positionedEdit.TextIndex[0].Runs!.Count == 3, "individual Tj and TJ operands exported");
        foreach (var (original, replacement) in new[] { ("Object ", "Item "), ("graph", "map"), (" tail", " a") })
        {
            PdfTextEditor.UpdateText(positionedEdit, 1, positionedEdit.TextIndex[0].Runs!.Single(r => r.Text == original), replacement);
        }

        using (var runPdf = NativeTestDocument.Open(PdfWriter.Write(positionedEdit)))
        {
            Check(runPdf.GetPage(1).Text == "Item map a", "successive direct edits within and across operators");
        }

        var forms = PdfReader.Read(CreateFixture(withForms: true));
        Check(forms.ImageIndex[0].Warning is null && forms.ImageIndex[0].Images.Count == 2, "nested form image indexed");
        var rotatedImage = forms.ImageIndex[0].Images[1];
        Check(rotatedImage.Corners[0] == new PdfImagePoint(100, 100) && rotatedImage.Corners[1] == new PdfImagePoint(100, 120), "rotated image corners preserved");
        Check(forms.TextIndex[0].Warning is null, "form indexing successful");
        Check(forms.TextIndex[0].ContentStreams.SequenceEqual([new PdfReference(10), new PdfReference(11)]), "content array references");
        Check(forms.TextIndex[0].FormStreams.SequenceEqual([new PdfReference(12), new PdfReference(13)]), "inherited nested form resources");
        Check(forms.TextIndex[0].Text.Contains("Nested form"), "nested form readable text");
        Reject(() => PdfTextEditor.UpdateText(forms, 1, new PdfReference(10), 0, -1, "Form"), "form edit explicitly rejected");
        var rebuilt = PdfWriter.Write(PdfConverter.FromJson(json));
        var again = PdfReader.Read(rebuilt);
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
            Check(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.JsonSerializer.SerializeToNode(obj.Value), System.Text.Json.JsonSerializer.SerializeToNode(other.Value)), $"object {obj.Id}");
        }

        Check(json.Contains("AQIDAP8="), "embedded binary encoded as Base64");
        Check(!json.Contains("originalPdfBase64"), "no whole-file payload");
        using (var parsed = NativeTestDocument.Open(rebuilt))
        {
            Check(parsed.GetPage(1).NumberOfImages == 1, "image XObject");
            Check(parsed.GetPage(1).Text.Contains("Object graph"), "text and font resources");
        }

        var info = graph.Objects.Single(o => o.Id.ObjectNumber == 9);
        info.Value.Entries!["Title"] = new()
        {
            Type = "string",
            Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("Edited metadata"))
        };
        using (var edited = NativeTestDocument.Open(PdfWriter.Write(graph)))
        {
            Check(edited.Information.Title == "Edited metadata", "metadata JSON edits");
        }

        graph.Objects.Add(graph.Objects[0]);
        Reject(() => PdfWriter.Write(graph), "duplicate object");
        graph.Objects.RemoveAt(graph.Objects.Count - 1);
        graph.Objects.RemoveAll(o => o.Id.ObjectNumber == 2);
        Reject(() => PdfWriter.Write(graph), "dangling reference");
        Reject(() => PdfConverter.FromJson("{\"format\":\"wrong\",\"trailer\":{\"type\":\"null\"}}"), "format");
        var compressed = PdfReader.Read(CreateCompressedFixture());
        Check(compressed.Objects.Count == 3, "object streams expanded, storage objects removed");
        using (var expanded = NativeTestDocument.Open(PdfWriter.Write(compressed)))
        {
            Check(expanded.NumberOfPages == 1, "xref stream and compressed objects round trip");
        }

        var incremental = PdfReader.Read(AddIncrementalUpdate(CreateFixture()));
        using (var updated = NativeTestDocument.Open(PdfWriter.Write(incremental)))
        {
            Check(updated.Information.Title == "Latest revision", "latest incremental revision wins");
        }

        Check(incremental.Trailer.Entries!.ContainsKey("Custom"), "trailer extension inherited");
        var streamObject = incremental.Objects.Single(o => o.Id.ObjectNumber == 10);
        Check(streamObject.Value.Entries!.ContainsKey("Filter"), "compressed content filter preserved");
        using (var independent = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(PdfWriter.Write(compressed)), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        {
            Check(independent.PageCount == 1, "independent reader after object stream expansion");
        }

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
            offsets.Add((n, gen, file.Position));
            Write($"{n} {gen} obj\n{body}\nendobj\n");
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
        foreach (var(n, gen, offset)in offsets)
        {
            Write($"{n} 1\n{offset:D10} {gen:D5} n \n");
        }

        Write($"trailer\n<< /Size {(withForms ? 14 : 11)} /Root 1 0 R /Info 9 0 R /ID [<010203> <040506>] /Custom (trailer extension) >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zip = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            zip.Write(data);
        }

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
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> >>"
        };
        var header = $"1 0 2 {bodies[0].Length + 1} 3 {bodies[0].Length + bodies[1].Length + 2} ";
        var payload = Compress(Encoding.ASCII.GetBytes(header + string.Join(" ", bodies)));
        using var output = new MemoryStream();
        void Write(string s) => output.Write(Encoding.ASCII.GetBytes(s));
        Write("%PDF-1.7\n");
        var objOffset = output.Position;
        Write($"4 0 obj\n<< /Type /ObjStm /N 3 /First {header.Length} /Filter /FlateDecode /Length {payload.Length} >>\nstream\n");
        output.Write(payload);
        Write("\nendstream\nendobj\n");
        var xrefOffset = output.Position;
        using var entries = new MemoryStream();
        void Entry(byte type, long field, int generation)
        {
            entries.WriteByte(type);
            for (var i = 3; i >= 0; i--)
            {
                entries.WriteByte((byte)(field >> (i * 8)));
            }

            entries.WriteByte((byte)(generation >> 8));
            entries.WriteByte((byte)generation);
        }

        Entry(0, 0, 65535);
        for (var i = 0; i < 3; i++)
        {
            Entry(2, 4, i);
        }

        Entry(1, objOffset, 0);
        Entry(1, xrefOffset, 0);
        var xref = Compress(entries.ToArray());
        Write($"5 0 obj\n<< /Type /XRef /Size 6 /Root 1 0 R /W [1 4 2] /Filter /FlateDecode /Length {xref.Length} >>\nstream\n");
        output.Write(xref);
        Write($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }
}
