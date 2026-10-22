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
        page.Dictionary.Entries["Contents"] = Add(Stream(Encoding.ASCII.GetBytes($"/Span {properties} BDC q 1 0 0 1 30 30 cm /Fm Do Q q 1 0 0 1 150 150 cm /Fm Do Q EMC")));
        var placements = PdfImageExtractor.Extract(document)[0].Images;
        Check(placements.Count == 2, "Shared form fixture");
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
        Console.WriteLine($"Passed {checks} page editing checks.");
    }
}
