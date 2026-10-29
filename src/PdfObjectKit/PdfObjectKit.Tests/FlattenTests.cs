using PdfObjectKit.Converters;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Parsers;
using PdfObjectKit.Writers;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class FlattenTests
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Flatten: " + message);
            }
        }
        var source = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Body"), new FixturePage("Second")));
        PdfFormEditor.InsertTextField(source, 1, "Name", "Field value", 30, 200, 150, 30);
        PdfFormEditor.InsertCheckBox(source, 2, "Approved", true, 30, 200, 20, 20);
        PdfAnnotationEditor.Add(source, 1, PdfAnnotationKind.Highlight, [new(20, 95, 80, 15)], "Comment message", "Reviewer");
        PdfMetadataEditor.Update(source, new() { Fields = new() { ["Title"] = "Flatten test" } });
        var original = PdfConverter.ToJson(source);
        var formsOnly = PdfFlattenEditor.Flatten(source, new() { Annotations = false });
        Check(PdfFormExtractor.Extract(formsOnly).Count == 0 && PdfAnnotationExtractor.Extract(formsOnly).Count == 1, "forms-only selection");
        Check(PdfTextExtractor.Extract(formsOnly)[0].Text.Contains("Field value"), "field text becomes page text");
        var annotationsOnly = PdfFlattenEditor.Flatten(source, new() { FormFields = false });
        Check(PdfFormExtractor.Extract(annotationsOnly).Count == 2 && PdfAnnotationExtractor.Extract(annotationsOnly).Count == 0, "annotations-only selection");
        var both = PdfReader.Read(PdfWriter.Write(PdfFlattenEditor.Flatten(source)));
        Check(PdfFormExtractor.Extract(both).Count == 0 && PdfAnnotationExtractor.Extract(both).Count == 0, "all interactivity removed");
        Check(PdfMetadataExtractor.Extract(both).Values["Title"] == "Flatten test", "metadata retained");
        Check(PdfConverter.ToJson(source) == original, "source unchanged");
        var graph = new PdfGraph(both);
        Check(graph.Pages().All(page => graph.Get(page.Dictionary, "Annots") is null), "annotation arrays removed");
        Check(!both.Objects.Any(item => item.Value.Entries?.GetValueOrDefault("Subtype")?.Name == "Widget"), "orphan widgets removed");
        Check(!both.Objects.Any(item => item.Value.Entries?.GetValueOrDefault("Subtype")?.Name == "Highlight"), "orphan markup annotation removed");
        var secondResources = graph.Get(graph.Pages()[1].Resources, "XObject")!;
        Check(secondResources.Entries!.Count > 0, "checkbox state saved as page resource");
        var originalGraph = new PdfGraph(source);
        var originalWidget = originalGraph.Resolve(originalGraph.Get(originalGraph.Pages()[1].Dictionary, "Annots")!.Items![0]);
        var stateName = originalGraph.Get(originalWidget, "AS")!.Name!;
        var selectedAppearance = originalGraph.Get(originalGraph.Get(originalGraph.Get(originalWidget, "AP")!, "N")!, stateName)!;
        Check(secondResources.Entries.Values.Select(graph.Resolve).Any(value => value.Base64 == selectedAppearance.Base64), "active checkbox appearance used");
        using (var independent = NativeTestDocument.Open(PdfWriter.Write(both)))
        {
            Check(independent.NumberOfPages == 2 && independent.GetPage(1).Text.Contains("Field value"), "independent PDF parse");
        }
        // Rotated normal appearances must remain transformed, not painted as upright replacements.
        var transformed = PdfConverter.FromJson(original);
        graph = new PdfGraph(transformed);
        var widget = graph.Resolve(graph.Get(graph.Pages()[0].Dictionary, "Annots")!.Items![0]);
        var appearance = graph.Get(graph.Get(widget, "AP")!, "N")!;
        appearance.Entries!["Matrix"] = Array(Num(0), Num(1), Num(-1), Num(0), Num(30), Num(0));
        PdfPageEditor.RotatePage(transformed, 1, 90);
        var transformedResult = PdfFlattenEditor.Flatten(transformed);
        graph = new PdfGraph(transformedResult);
        Check(graph.Number(graph.Pages()[0].Dictionary, "Rotate") == 90, "page rotation retained");
        Check(PdfTextExtractor.Extract(transformedResult)[0].Text.Contains("Field value"), "rotated appearance remains readable");
        var missing = PdfConverter.FromJson(original);
        graph = new PdfGraph(missing);
        graph.Resolve(graph.Get(graph.Pages()[0].Dictionary, "Annots")!.Items![0]).Entries!.Remove("AP");
        var beforeFailure = PdfConverter.ToJson(missing);
        try
        {
            PdfFlattenEditor.Flatten(missing);
            throw new Exception("Missing appearance accepted");
        }
        catch (NotSupportedException)
        {
            Check(PdfConverter.ToJson(missing) == beforeFailure, "rejection is atomic");
        }
        var needsAppearances = PdfConverter.FromJson(original);
        graph = new PdfGraph(needsAppearances);
        graph.Get(graph.Resolve(needsAppearances.Trailer.Entries!["Root"]), "AcroForm")!.Entries!["NeedAppearances"] = new() { Type = "boolean", Boolean = true };
        try
        {
            PdfFlattenEditor.Flatten(needsAppearances);
            throw new Exception("Stale appearances accepted");
        }
        catch (NotSupportedException)
        {
            // Do not trust a document requesting regenerated appearances.
        }
        try
        {
            PdfFlattenEditor.Flatten(source, new() { FormFields = false, Annotations = false });
            throw new Exception("Empty selection accepted");
        }
        catch (ArgumentException)
        {
            // An explicit operation is required.
        }
        var signed = PdfConverter.FromJson(original);
        signed.Objects.Add(new(new(signed.Objects.Max(item => item.Id.ObjectNumber) + 1), Dict(("Type", Name("Sig")), ("ByteRange", Array(Num(0), Num(10), Num(20), Num(10))))));
        try
        {
            PdfFlattenEditor.Flatten(signed);
            throw new Exception("Signed document accepted");
        }
        catch (NotSupportedException)
        {
            // Includes standalone timestamp/signature dictionaries, not only field signatures.
        }
        var tagged = PdfConverter.FromJson(original);
        graph = new PdfGraph(tagged);
        var catalog = graph.Resolve(tagged.Trailer.Entries!["Root"]);
        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(tagged.Objects.Max(item => item.Id.ObjectNumber) + 1);
            tagged.Objects.Add(new(id, value));
            return Ref(id);
        }
        var root = Dict(("Type", Name("StructTreeRoot")));
        var rootRef = Add(root);
        catalog.Entries!["StructTreeRoot"] = rootRef;
        catalog.Entries["MarkInfo"] = Dict(("Marked", new PdfValue { Type = "boolean", Boolean = true }));
        var taggedPages = graph.Pages();
        PdfValue PageRef(int index) => Ref(tagged.Objects.Single(item => ReferenceEquals(item.Value, taggedPages[index].Dictionary)).Id);
        taggedPages[0].Dictionary.Entries!["StructParents"] = Num(0);
        var content = taggedPages[0].Streams[0].Value;
        content.Base64 = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("/P <</MCID 0>> BDC\n" + System.Text.Encoding.ASCII.GetString(PdfStreamDecoder.Decode(content)) + "\nEMC"));
        content.Entries!.Remove("Filter");
        content.Entries.Remove("DecodeParms");
        var body = Add(Dict(("Type", Name("StructElem")), ("S", Name("P")), ("P", rootRef), ("Pg", PageRef(0)), ("K", Num(0))));
        var elements = new List<PdfValue> { body };
        var nums = new List<PdfValue> { Num(0), Array(body) };
        for (var i = 0; i < 2; i++)
        {
            var rawWidget = graph.Get(taggedPages[i].Dictionary, "Annots")!.Items![0];
            graph.Resolve(rawWidget).Entries!["StructParent"] = Num(7 + i);
            var element = Add(Dict(("Type", Name("StructElem")), ("S", Name("Form")), ("P", rootRef), ("Pg", PageRef(i)),
                ("K", Dict(("Type", Name("OBJR")), ("Obj", rawWidget), ("Pg", PageRef(i)))), ("Alt", Str("Field label"u8.ToArray()))));
            elements.Add(element);
            nums.Add(Num(7 + i));
            nums.Add(element);
        }
        root.Entries!["K"] = Array(elements.ToArray());
        root.Entries["ParentTree"] = Dict(("Kids", Array(Add(Dict(("Nums", Array(nums.ToArray())))))));
        root.Entries["ParentTreeNextKey"] = Num(9);
        var taggedBefore = PdfConverter.ToJson(tagged);
        var taggedFlat = PdfReader.Read(PdfWriter.Write(PdfFlattenEditor.Flatten(tagged)));
        Check(PdfConverter.ToJson(tagged) == taggedBefore, "tagged source is unchanged");
        graph = new PdfGraph(taggedFlat);
        var flatRoot = graph.Get(graph.Resolve(taggedFlat.Trailer.Entries!["Root"]), "StructTreeRoot")!;
        var flatKids = graph.Get(flatRoot, "K")!.Items!;
        Check(flatKids.Count == 3 && graph.Get(graph.Resolve(flatKids[0]), "K")!.Number == "0", "existing body tag retained");
        var reverse = graph.Get(graph.Get(flatRoot, "ParentTree")!, "Nums")!.Items!;
        Check(reverse.Where((_, i) => i % 2 == 0).Select(value => value.Number).SequenceEqual(new[] { "0", "9", "10" }), "annotation parent entries replaced without colliding with page tags");
        foreach (var element in flatKids.Skip(1).Select(graph.Resolve))
        {
            var mcr = graph.Get(element, "K")!;
            Check(graph.Get(mcr, "Type")!.Name == "MCR" && graph.Get(mcr, "MCID")!.Number == "0", "OBJR converted to marked content");
            var wrapper = graph.Get(mcr, "Stm")!;
            Check(graph.Get(wrapper, "StructParents") is not null && System.Text.Encoding.ASCII.GetString(PdfStreamDecoder.Decode(wrapper)).Contains("/MCID 0"), "appearance marked content registered");
            Check(graph.Get(element, "Alt") is not null, "accessibility label preserved");
        }
        // Assembly independently checks forward/reverse structure mappings, including Form MCIDs.
        var reassembled = PdfPageEditor.ExtractPages(taggedFlat, [2, 1]);
        Check(PdfTextExtractor.Extract(reassembled)[1].Text.Contains("Field value"), "flattened tags survive subsequent page assembly");
        Console.WriteLine("Flatten selection, appearance, tagged structure remapping, cleanup and atomicity checks passed.");
    }
}
