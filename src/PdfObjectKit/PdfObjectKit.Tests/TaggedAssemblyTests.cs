using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Editors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class TaggedAssemblyTests
{
    public static void Run()
    {
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new Exception("Tagged assembly: " + message);
            }
        }
        var source = PdfReader.Read(PdfFixture.Create(null, new("First tagged"), new("Second tagged")));
        var graph = new PdfGraph(source);
        var catalog = graph.Resolve(source.Trailer.Entries!["Root"]);
        var pages = graph.Pages();
        var pageRefs = graph.Get(catalog, "Pages")!.Entries!["Kids"].Items!;
        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(source.Objects.Max(item => item.Id.ObjectNumber) + 1);
            source.Objects.Add(new(id, value));
            return Ref(id);
        }
        var root = Dict(("Type", Name("StructTreeRoot")));
        var rootRef = Add(root);
        var document = Dict(("Type", Name("StructElem")), ("S", Name("Document")), ("P", rootRef));
        var documentRef = Add(document);
        var elements = new List<PdfValue>();
        for (var i = 0; i < 2; i++)
        {
            pages[i].Dictionary.Entries!["StructParents"] = Num(i);
            pages[i].Dictionary.Entries!["Tabs"] = Name("S");
            var stream = pages[i].Streams[0].Value;
            stream.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("/P <</MCID 0>> BDC\n")
                .Concat(Convert.FromBase64String(stream.Base64!)).Concat(Encoding.ASCII.GetBytes("\nEMC")).ToArray());
            var element = Dict(("Type", Name("StructElem")), ("S", Name("CustomParagraph")), ("P", documentRef),
                ("Pg", pageRefs[i]), ("K", Num(0)), ("C", Name("Body")), ("ID", Str(Encoding.ASCII.GetBytes("p" + i))),
                ("ActualText", Str(Encoding.ASCII.GetBytes("Semantic " + i))));
            elements.Add(Add(element));
        }
        var annotation = Dict(("Type", Name("Annot")), ("Subtype", Name("Link")), ("Rect", Array(Num(0), Num(0), Num(30), Num(30))),
            ("P", pageRefs[0]), ("StructParent", Num(2)), ("A", Dict(("S", Name("URI")), ("URI", Str(Encoding.ASCII.GetBytes("https://example.com"))))));
        var annotationRef = Add(annotation);
        pages[0].Dictionary.Entries!["Annots"] = Array(annotationRef);
        var linkElement = Add(Dict(("Type", Name("StructElem")), ("S", Name("Link")), ("P", documentRef),
            ("Pg", pageRefs[0]), ("K", Dict(("Type", Name("OBJR")), ("Obj", annotationRef)))));
        var form = Stream(Encoding.ASCII.GetBytes("/Figure <</MCID 0>> BDC 0 0 10 10 re f EMC"));
        form.Entries!["Type"] = Name("XObject");
        form.Entries["Subtype"] = Name("Form");
        form.Entries["BBox"] = Array(Num(0), Num(0), Num(10), Num(10));
        form.Entries["StructParents"] = Num(3);
        var formRef = Add(form);
        pages[0].Resources.Entries!["XObject"] = Dict(("Figure", formRef));
        pages[0].Dictionary.Entries!["Contents"] = Array(pages[0].Dictionary.Entries!["Contents"],
            Add(Stream(Encoding.ASCII.GetBytes("q 1 0 0 1 50 50 cm /Figure Do Q"))));
        var figureElement = Add(Dict(("Type", Name("StructElem")), ("S", Name("Figure")), ("P", documentRef),
            ("Pg", pageRefs[0]), ("Alt", Str(Encoding.ASCII.GetBytes("A square"))),
            ("K", Dict(("Type", Name("MCR")), ("Stm", formRef), ("MCID", Num(0))))));
        document.Entries!["K"] = Array(elements[0], linkElement, figureElement, elements[1]);
        root.Entries!["K"] = Array(documentRef);
        root.Entries["RoleMap"] = Dict(("CustomParagraph", Name("P")));
        root.Entries["ClassMap"] = Dict(("Body", Dict(("O", Name("Layout")), ("Placement", Name("Block")))));
        var parentLeaf1 = Add(Dict(("Nums", Array(Num(0), Array(elements[0]), Num(1), Array(elements[1])))));
        var parentLeaf2 = Add(Dict(("Nums", Array(Num(2), linkElement, Num(3), Array(figureElement)))));
        root.Entries["ParentTree"] = Dict(("Kids", Array(parentLeaf1, parentLeaf2)));
        root.Entries["ParentTreeNextKey"] = Num(4);
        catalog.Entries!["StructTreeRoot"] = rootRef;
        catalog.Entries["MarkInfo"] = Dict(("Marked", new PdfValue { Type = "boolean", Boolean = true }));
        var before = PdfConverter.ToJson(source);

        var second = PdfPageEditor.ExtractPages(source, [2]);
        var secondGraph = new PdfGraph(second);
        var secondRoot = secondGraph.Get(secondGraph.Resolve(second.Trailer.Entries!["Root"]), "StructTreeRoot")!;
        var secondDocument = secondGraph.Resolve(secondGraph.Get(secondRoot, "K")!.Items!.Single());
        var paragraph = secondGraph.Resolve(secondGraph.Get(secondDocument, "K")!.Items!.Single());
        Check(ReferenceEquals(secondGraph.Get(paragraph, "Pg"), secondGraph.Pages()[0].Dictionary), "retained element points at extracted page");
        Check(PdfFormGraph.Text(secondGraph.Get(paragraph, "ActualText")!) == "Semantic 1", "actual text preserved");
        Check(secondGraph.Get(paragraph, "S")?.Name == "Assembly1_CustomParagraph", "custom role namespaced");
        Check(secondGraph.Get(secondRoot, "RoleMap")!.Entries!["Assembly1_CustomParagraph"].Name == "P", "role meaning retained");
        var secondParents = secondGraph.Get(secondGraph.Get(secondRoot, "ParentTree")!, "Nums")!.Items!;
        Check(secondParents.Count == 2 && ReferenceEquals(secondGraph.Resolve(secondParents[1].Items![0]), paragraph), "parent array points at same structure element");
        Check(secondGraph.Get(secondRoot, "IDTree") is not null, "ID tree rebuilt");

        var first = PdfPageEditor.ExtractPages(source, [1]);
        var firstGraph = new PdfGraph(first);
        var firstRoot = firstGraph.Get(firstGraph.Resolve(first.Trailer.Entries!["Root"]), "StructTreeRoot")!;
        var firstParents = firstGraph.Get(firstGraph.Get(firstRoot, "ParentTree")!, "Nums")!.Items!;
        Check(firstParents.Count == 6, "page, annotation and form parent keys retained");
        var firstDocument = firstGraph.Resolve(firstGraph.Get(firstRoot, "K")!.Items![0]);
        Check(firstGraph.Get(firstDocument, "K")!.Items!.Count == 3, "other page structure pruned");
        var figure = firstGraph.Resolve(firstGraph.Get(firstDocument, "K")!.Items![2]);
        Check(PdfFormGraph.Text(firstGraph.Get(figure, "Alt")!) == "A square", "figure alt text retained");
        using (var independent = NativeTestDocument.Open(PdfWriter.Write(first)))
        {
            Check(independent.GetPage(1).Text == "First tagged", "drawing content unchanged");
        }
        var merged = PdfDocumentMerger.Merge([source, source]);
        var mergedGraph = new PdfGraph(merged);
        var mergedRoot = mergedGraph.Get(mergedGraph.Resolve(merged.Trailer.Entries!["Root"]), "StructTreeRoot")!;
        Check(mergedGraph.Get(mergedRoot, "K")!.Items!.Count == 2, "two document structure roots retained");
        var mergedParents = mergedGraph.Get(mergedGraph.Get(mergedRoot, "ParentTree")!, "Nums")!.Items!;
        Check(mergedParents.Count == 16 && Enumerable.Range(0, 8).Select(i => mergedParents[i * 2].Number).Distinct().Count() == 8, "merged parent keys unique");
        Check(mergedGraph.Get(mergedRoot, "RoleMap")!.Entries!.Count == 2, "role map collisions resolved");
        foreach (var child in mergedGraph.Get(mergedRoot, "K")!.Items!)
        {
            Check(ReferenceEquals(mergedGraph.Get(child, "P"), mergedRoot), "merged root parent pointer");
        }
        var reordered = PdfPageEditor.ExtractPages(source, [2, 1]);
        using var reorderedRead = NativeTestDocument.Open(PdfWriter.Write(reordered));
        Check(reorderedRead.GetPage(1).Text == "Second tagged" && reorderedRead.GetPage(2).Text == "First tagged", "page order retained");
        Check(PdfConverter.ToJson(source) == before, "source graph untouched");
        var broken = PdfConverter.FromJson(before);
        var brokenGraph = new PdfGraph(broken);
        brokenGraph.Pages()[0].Dictionary.Entries!["StructParents"] = Num(99);
        try
        {
            PdfPageEditor.ExtractPages(broken, [1]);
            throw new Exception("Expected invalid parent tree rejection.");
        }
        catch (InvalidDataException)
        {
        }
        if (Environment.GetEnvironmentVariable("PDFOBJECTKIT_TAGGED_TEST_PDF") is { Length: > 0 } fixturePath)
        {
            var real = PdfReader.Read(File.ReadAllBytes(fixturePath));
            var count = 0;
            foreach (var part in PdfPageEditor.SplitPages(real))
            {
                using var independentPart = NativeTestDocument.Open(PdfWriter.Write(part));
                Check(independentPart.NumberOfPages == 1, "real PDF split page count");
                count++;
            }
            Check(count == new PdfGraph(real).Pages().Count, "real PDF split count");
            var realGraph = new PdfGraph(real);
            var taggedInput = realGraph.Get(realGraph.Resolve(real.Trailer.Entries!["Root"]), "StructTreeRoot") is not null;
            Console.WriteLine($"Real PDF split checks passed ({count} pages, tagged: {taggedInput}).");
        }
        Console.WriteLine("Tagged page assembly checks passed.");
    }
}
