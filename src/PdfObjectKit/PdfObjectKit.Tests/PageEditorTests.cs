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

internal static class PageEditorTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new Exception(message);
            }

            checks++;
        }

        var rotated = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Rotate"), new FixturePage("Sibling")));
        var rotationGraph = new PdfGraph(rotated);
        rotationGraph.Get(rotationGraph.Pages()[0].Dictionary, "Parent")!.Entries!["Rotate"] = Num(90);
        PdfPageEditor.RotatePage(rotated, 1, 90);
        rotationGraph = new PdfGraph(PdfReader.Read(PdfWriter.Write(rotated)));
        Check(rotationGraph.Number(rotationGraph.Pages()[0].Dictionary, "Rotate") == 180, "Rotation includes inherited value");
        Check(rotationGraph.Get(rotationGraph.Pages()[1].Dictionary, "Rotate") is null, "Sibling rotation remains inherited");
        PdfPageEditor.RotatePage(rotated, 1, -270);
        Check(new PdfGraph(rotated).Number(new PdfGraph(rotated).Pages()[0].Dictionary, "Rotate") == 270, "Negative rotation normalized");
        var rotationBefore = PdfConverter.ToJson(rotated);
        foreach (var invalid in new[] { (Page: 1, Degrees: 45), (Page: 0, Degrees: 90), (Page: 3, Degrees: 90) })
        {
            try
            {
                PdfPageEditor.RotatePage(rotated, invalid.Page, invalid.Degrees);
                throw new Exception("Invalid rotation accepted");
            }
            catch (ArgumentException)
            {
                Check(PdfConverter.ToJson(rotated) == rotationBefore, "Invalid rotation is atomic");
            }
        }

        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
        PdfPageEditor.InsertText(document, 1, "Added", 40, 40, 12);
        var text = PdfTextExtractor.Extract(document)[0];
        Check(text.Text.Contains("Original") && text.Text.Contains("Added"), "Insertion preserves original text");
        PdfTextEditor.UpdateText(document, 1, text.Runs!.Last(), "");
        Check(!PdfTextExtractor.Extract(document)[0].Text.Contains("Added"), "Inserted text is deletable");
        var before = PdfConverter.ToJson(document);
        try
        {
            PdfPageEditor.InsertText(document, 1, "\u4e2d", 1, 2);
            throw new Exception("Unsupported text accepted");
        }
        catch (ArgumentException)
        {
            Check(before == PdfConverter.ToJson(document), "Failed insertion is atomic");
        }

        PdfFormEditor.InsertTextField(document, 1, "One", "First", 20, 20, 100, 20);
        PdfFormEditor.InsertTextField(document, 1, "Two", "Second", 20, 60, 100, 20);
        PdfFormEditor.DeleteField(document, "One");
        var fields = PdfFormExtractor.Extract(PdfReader.Read(PdfWriter.Write(document)));
        Check(fields.Count == 1 && fields[0].Name == "Two" && fields[0].Values[0] == "Second", "Deleting a field preserves other fields");
        Check(new PdfGraph(document).Get(new PdfGraph(document).Pages()[0].Dictionary, "Annots")!.Items!.Count == 1, "Deleted widget removed from annotations");

        PdfFormEditor.MoveWidget(document, "Two", 0, 12, -5);
        var movedField = PdfFormExtractor.Extract(PdfReader.Read(PdfWriter.Write(document))).Single();
        Check(movedField.Widgets[0].Rectangle.SequenceEqual(new double[] { 32, 55, 132, 75 }), "Widget movement preserves dimensions");
        Check(movedField.Values[0] == "Second", "Widget movement preserves value");

        var graph = new PdfGraph(document);
        var page = graph.Pages()[0];
        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(document.Objects.Max(o => o.Id.ObjectNumber) + 1);
            document.Objects.Add(new(id, value));
            return Ref(id);
        }

        var image = Stream(new byte[] { 255, 0, 0 });
        image.Entries!["Subtype"] = Name("Image");
        image.Entries["Width"] = Num(1);
        image.Entries["Height"] = Num(1);
        image.Entries["ColorSpace"] = Name("DeviceRGB");
        image.Entries["BitsPerComponent"] = Num(8);
        var imageRef = Add(image);
        const string properties = "<< /MCID 7 /ActualText <00FF> /Nested << /Key#20Name [(label) true null /A#23B] >> >>";
        var form = Stream(Encoding.ASCII.GetBytes($"/Figure {properties} BDC q 20 0 0 20 0 0 cm /Im Do Q EMC"));
        form.Entries!["Subtype"] = Name("Form");
        form.Entries["BBox"] = Array(Num(0), Num(0), Num(100), Num(100));
        form.Entries["Resources"] = Dict(("XObject", Dict(("Im", imageRef))));
        var formRef = Add(form);
        page.Dictionary.Entries!["Resources"] = Dict(("XObject", Dict(("Fm", formRef))));
        page.Dictionary.Entries["Contents"] = Add(Stream(Encoding.ASCII.GetBytes($"/Span {properties} BDC q 2 0 0 1 30 30 cm /Fm Do Q q 1 0 0 1 150 150 cm /Fm Do Q EMC")));
        var placements = PdfImageExtractor.Extract(document)[0].Images;
        Check(placements.Count == 2, "Shared form fixture");
        var movedDocument = PdfConverter.FromJson(PdfConverter.ToJson(document));
        PdfImageEditor.MoveImage(movedDocument, 1, 1, 12, 7);
        var movedImages = PdfImageExtractor.Extract(PdfReader.Read(PdfWriter.Write(movedDocument)))[0].Images;
        Check(movedImages[1].Corners.SequenceEqual(placements[1].Corners), "Moving image preserves shared occurrence");
        Check(movedImages[0].EncodedDataSha256 == placements[0].EncodedDataSha256, "Moving image preserves bytes");
        Check(movedImages[0].Corners.Zip(placements[0].Corners).All(pair => Math.Abs(pair.First.X - pair.Second.X - 12) < .001 && Math.Abs(pair.First.Y - pair.Second.Y - 7) < .001), "Moving transformed image uses page coordinates");

        var replacementBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        foreach (var fit in Enum.GetValues<PdfImageFit>())
        {
            var edited = PdfConverter.FromJson(PdfConverter.ToJson(document));
            PdfImageEditor.ReplaceImage(edited, 1, 1, replacementBytes, fit);
            var replaced = PdfImageExtractor.Extract(PdfReader.Read(PdfWriter.Write(edited)))[0];
            Check(replaced.Warning is null && replaced.Images.Count == 2, "Replacement retains occurrence count " + fit);
            Check(replaced.Images[1].Corners.SequenceEqual(placements[1].Corners) && replaced.Images[1].EncodedDataSha256 == placements[1].EncodedDataSha256, "Replacement preserves shared occurrence " + fit);
            Check(edited.Objects.Single(o => o.Id == formRef.Reference).Value.Base64 == form.Base64, "Replacement leaves original shared form intact");
            var points = replaced.Images[0].Corners;
            Check(Math.Abs(points[1].X - points[0].X - (fit == PdfImageFit.Fit ? 20 : 40)) < .001, "Replacement horizontal scaling " + fit);
            Check(Math.Abs(points[3].Y - points[0].Y - (fit == PdfImageFit.Fill ? 40 : 20)) < .001, "Replacement vertical scaling " + fit);
            Check(Math.Abs((points[0].X + points[2].X) / 2 - 50) < .001 && Math.Abs((points[0].Y + points[2].Y) / 2 - 40) < .001, "Replacement centered " + fit);
        }

        var beforeReplacement = PdfConverter.ToJson(document);
        try
        {
            PdfImageEditor.ReplaceImage(document, 1, 999, replacementBytes);
            throw new Exception("Invalid image number accepted");
        }
        catch (NotSupportedException)
        {
            Check(PdfConverter.ToJson(document) == beforeReplacement, "Failed replacement is atomic");
        }

        PdfPageEditor.DeleteImage(document, 1, 1);
        var remaining = PdfImageExtractor.Extract(PdfReader.Read(PdfWriter.Write(document)))[0];
        Check(remaining.Warning is null && remaining.Images.Count == 1 && remaining.Images[0].Corners.SequenceEqual(placements[1].Corners), "Deleting nested occurrence preserves other shared form placement");
        Check(document.Objects.Single(o => o.Id == formRef.Reference).Value.Base64 == form.Base64, "Shared source form unchanged");
        var rebuilt = new PdfGraph(PdfReader.Read(PdfWriter.Write(document)));
        var rebuiltPage = rebuilt.Pages()[0];
        var clonedForm = rebuilt.Get(rebuiltPage.Resources, "XObject")!.Entries!.Values
            .Select(rebuilt.Resolve).Single(v => v.Base64 != form.Base64);
        foreach (var stream in new[] { rebuiltPage.Streams.Single().Value, clonedForm })
        {
            var operations = PdfContent.Parse(PdfStreamDecoder.Decode(stream));
            var dictionary = operations.Single(o => o.Operator == "BDC").Operands[1];
            Check(dictionary.Entries!["MCID"].Number == "7" && dictionary.Entries["ActualText"].Base64 == "AP8=", "Marked-content dictionary and binary string preserved");
            var nested = dictionary.Entries["Nested"].Entries!["Key Name"].Items!;
            Check(nested.Count == 4 && nested[1].Boolean == true && nested[2].Type == "null" && nested[3].Name == "A#B", "Nested dictionary, array and escaped names preserved");
            Check(operations.Last().Operator == "EMC", "Marked-content boundaries preserved");
        }
        var inlineDocument = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Inline")));
        var inlinePage = new PdfGraph(inlineDocument).Pages()[0];
        var inlineStream = inlinePage.Streams[0].Value;
        inlineStream.Entries!.Remove("Filter");
        inlineStream.Entries.Remove("DecodeParms");
        inlineStream.Entries.Remove("Length");
        inlineStream.Base64 = Convert.ToBase64String(Encoding.Latin1.GetBytes("q 0 20 -40 0 100 30 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID ").Concat(new byte[] { 255, 0, 0 }).Concat(Encoding.ASCII.GetBytes("\nEI Q")).ToArray());
        var inlineBefore = PdfImageExtractor.Extract(inlineDocument)[0].Images.Single();
        Check(inlineBefore.IsInline, "Inline fixture indexed");
        PdfImageEditor.ReplaceImage(inlineDocument, 1, 1, replacementBytes, PdfImageFit.Stretch);
        var inlineAfter = PdfImageExtractor.Extract(PdfReader.Read(PdfWriter.Write(inlineDocument)))[0].Images.Single();
        Check(!inlineAfter.IsInline && inlineAfter.Corners.SequenceEqual(inlineBefore.Corners), "Inline replacement preserves rotation and placement");

        Console.WriteLine($"Passed {checks} page editing checks.");
    }
}
