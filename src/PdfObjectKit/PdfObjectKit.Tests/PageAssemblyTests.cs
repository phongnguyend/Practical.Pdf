using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Options;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class PageAssemblyTests
{
    public static void Run()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Page assembly: " + message);
            }
        }
        void Reject(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return;
            }
            throw new Exception("Expected page assembly rejection.");
        }
        var source = PdfReader.Read(PdfFixture.Create(null, new("First"), new("Second"), new("Third")));
        var before = PdfConverter.ToJson(source);
        var extracted = PdfPageEditor.ExtractPages(source, [3, 1]);
        using (var independent = NativeTestDocument.Open(PdfWriter.Write(extracted)))
        {
            Check(independent.NumberOfPages == 2 && independent.GetPage(1).Text == "Third"
                && independent.GetPage(2).Text == "First", "selected page order");
        }
        Check(PdfConverter.ToJson(source) == before, "input unchanged");
        Check(extracted.Objects.Count < source.Objects.Count, "unselected dependencies omitted");
        var split = PdfPageEditor.Split(source);
        Check(split.Count == 3 && split.All(item => new PdfGraph(item).Pages().Count == 1), "split into single pages");
        var merged = PdfDocumentMerger.Merge([split[2], source, split[0]]);
        using (var independent = NativeTestDocument.Open(PdfWriter.Write(merged)))
        {
            Check(independent.NumberOfPages == 5 && independent.GetPage(1).Text == "Third"
                && independent.GetPage(5).Text == "First", "merge with colliding source IDs");
        }
        PdfPageEditor.InsertText(extracted, 1, "New", 10, 10);
        Check(PdfConverter.ToJson(source) == before, "no shared mutable objects");
        Reject(() => PdfPageEditor.ExtractPages(source, []));
        Reject(() => PdfPageEditor.ExtractPages(source, [0]));
        Reject(() => PdfPageEditor.ExtractPages(source, [4]));
        Reject(() => PdfPageEditor.ExtractPages(source, [1, 1]));
        Reject(() => PdfDocumentMerger.Merge([]));

        var graph = new PdfGraph(source);
        var pages = graph.Pages();
        var tree = graph.Resolve(source.Objects.Single(item => item.Id.ObjectNumber == 2).Value);
        tree.Entries!["Rotate"] = Num(90);
        tree.Entries["CropBox"] = Array(Num(10), Num(20), Num(300), Num(400));
        tree.Entries["MediaBox"] = pages[0].Dictionary.Entries!["MediaBox"];
        tree.Entries["Resources"] = pages[0].Dictionary.Entries!["Resources"];
        foreach (var page in pages)
        {
            page.Dictionary.Entries!.Remove("MediaBox");
            page.Dictionary.Entries.Remove("Resources");
        }
        var inherited = new PdfGraph(PdfPageEditor.ExtractPages(source, [2])).Pages()[0].Dictionary;
        Check(inherited.Entries!["Rotate"].Number == "90" && inherited.Entries["CropBox"].Items!.Count == 4
            && inherited.Entries.ContainsKey("Resources") && inherited.Entries.ContainsKey("MediaBox"), "inherited page attributes");

        // Explicit page destinations and annotation /P cycles are remapped.
        var firstRef = tree.Entries["Kids"].Items![0];
        var secondRef = tree.Entries["Kids"].Items![1];
        pages[0].Dictionary.Entries!["Annots"] = Array(Dict(("Type", Name("Annot")), ("Subtype", Name("Link")),
            ("Rect", Array(Num(0), Num(0), Num(10), Num(10))), ("P", firstRef), ("Dest", Array(secondRef, Name("Fit")))));
        var linked = PdfPageEditor.ExtractPages(source, [2, 1]);
        var linkedGraph = new PdfGraph(linked);
        var linkedPages = linkedGraph.Pages();
        var annotation = linkedGraph.Get(linkedPages[1].Dictionary, "Annots")!.Items![0];
        Check(ReferenceEquals(linkedGraph.Resolve(annotation.Entries!["Dest"].Items![0]), linkedPages[0].Dictionary), "link destination remapped");
        Check(ReferenceEquals(linkedGraph.Resolve(annotation.Entries["P"]), linkedPages[1].Dictionary), "annotation owner remapped");
        Reject(() => PdfPageEditor.ExtractPages(source, [1]));
        pages[0].Dictionary.Entries!.Remove("Annots");

        // A nested Form and image survive without decoding or recompressing their streams.
        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(source.Objects.Max(item => item.Id.ObjectNumber) + 1);
            source.Objects.Add(new(id, value));
            return Ref(id);
        }
        var image = Stream([255, 0, 0]);
        image.Entries!["Type"] = Name("XObject");
        image.Entries["Subtype"] = Name("Image");
        image.Entries["Width"] = Num(1);
        image.Entries["Height"] = Num(1);
        image.Entries["ColorSpace"] = Name("DeviceRGB");
        image.Entries["BitsPerComponent"] = Num(8);
        var form = Stream(Encoding.ASCII.GetBytes("q 10 0 0 10 0 0 cm /Im Do Q"));
        form.Entries!["Type"] = Name("XObject");
        form.Entries["Subtype"] = Name("Form");
        form.Entries["BBox"] = Array(Num(0), Num(0), Num(20), Num(20));
        form.Entries["Resources"] = Dict(("XObject", Dict(("Im", Add(image)))));
        tree.Entries["Resources"].Entries!["XObject"] = Dict(("Fm", Add(form)));
        pages[0].Dictionary.Entries!["Contents"] = Add(Stream(Encoding.ASCII.GetBytes("/Fm Do")));
        var images = PdfDocumentMerger.Merge([source, source]);
        var roundtrip = PdfReader.Read(PdfWriter.Write(images));
        Check(PdfImageExtractor.Extract(roundtrip)[0].Images.Count == 1 && PdfImageExtractor.Extract(roundtrip)[3].Images.Count == 1, "nested images survive merge");
        Check(PdfImageExtractor.Extract(roundtrip)[0].Images[0].EncodedDataSha256
            == PdfImageExtractor.Extract(source)[0].Images[0].EncodedDataSha256, "image bytes preserved");

        var catalog = graph.Resolve(source.Trailer.Entries!["Root"]);
        foreach (var key in new[] { "Perms" })
        {
            catalog.Entries![key] = Dict();
            Reject(() => PdfPageEditor.ExtractPages(source, [1]));
            catalog.Entries.Remove(key);
        }

        var forms = PdfReader.Read(PdfFixture.Create(null, new("One"), new("Two")));
        PdfFormEditor.InsertTextField(forms, 1, "Name", "Alice", 10, 10, 100, 20);
        PdfFormEditor.InsertTextField(forms, 2, "Address", "Street", 10, 10, 100, 20);
        var formBefore = PdfConverter.ToJson(forms);
        var firstFormPage = PdfPageEditor.ExtractPages(forms, [1]);
        Check(PdfFormExtractor.Extract(firstFormPage).Count == 1, "exclude fields on removed pages");
        PdfFormEditor.SetValue(firstFormPage, "Name", "Bob");
        Check(PdfFormExtractor.Extract(PdfReader.Read(PdfWriter.Write(firstFormPage)))[0].Values[0] == "Bob", "extracted field remains editable");
        var mergedForms = PdfDocumentMerger.Merge([firstFormPage, PdfPageEditor.ExtractPages(forms, [2])]);
        PdfFormEditor.SetValue(mergedForms, "Address", "New address");
        var mergedFields = PdfFormExtractor.Extract(PdfReader.Read(PdfWriter.Write(mergedForms)));
        Check(mergedFields.Count == 2 && mergedFields.Single(field => field.Name == "Address").Widgets[0].PageNumber == 2, "merged field ownership");
        Check(PdfConverter.ToJson(forms) == formBefore, "form sources unchanged");
        Reject(() => PdfDocumentMerger.Merge([firstFormPage, firstFormPage]));

        var navigationSource = PdfReader.Read(PdfFixture.Create(null, new("One"), new("Two")));
        var navGraph = new PdfGraph(navigationSource);
        var navCatalog = navGraph.Resolve(navigationSource.Trailer.Entries!["Root"]);
        var navPages = navGraph.Get(navCatalog, "Pages")!.Entries!["Kids"].Items!;
        PdfValue NavAdd(PdfValue value)
        {
            var id = new PdfReference(navigationSource.Objects.Max(item => item.Id.ObjectNumber) + 1);
            navigationSource.Objects.Add(new(id, value));
            return Ref(id);
        }
        var outlineRoot = Dict(("Type", Name("Outlines")), ("Count", Num(2)));
        var outlineRootRef = NavAdd(outlineRoot);
        var one = Dict(("Title", Str(Encoding.ASCII.GetBytes("One"))), ("Parent", outlineRootRef), ("Dest", Name("first")));
        var two = Dict(("Title", Str(Encoding.ASCII.GetBytes("Two"))), ("Parent", outlineRootRef), ("Dest", Array(navPages[1], Name("Fit"))));
        var oneRef = NavAdd(one);
        var twoRef = NavAdd(two);
        one.Entries!["Next"] = twoRef;
        two.Entries!["Prev"] = oneRef;
        outlineRoot.Entries!["First"] = oneRef;
        outlineRoot.Entries["Last"] = twoRef;
        navCatalog.Entries!["Outlines"] = outlineRootRef;
        navCatalog.Entries["Dests"] = Dict(("first", Array(navPages[0], Name("Fit"))));
        var bookmarkPage = PdfPageEditor.ExtractPages(navigationSource, [1]);
        var bookmarkGraph = new PdfGraph(bookmarkPage);
        var bookmarkCatalog = bookmarkGraph.Resolve(bookmarkPage.Trailer.Entries!["Root"]);
        var bookmarkRoot = bookmarkGraph.Get(bookmarkCatalog, "Outlines")!;
        Check(bookmarkGraph.Number(bookmarkRoot, "Count") == 1, "bookmarks on excluded pages pruned");
        var bookmark = bookmarkGraph.Get(bookmarkRoot, "First")!;
        Check(bookmarkGraph.Get(bookmark, "Dest")?.Type == "array", "named bookmark destination resolved");
        var navMerged = PdfDocumentMerger.Merge([bookmarkPage, bookmarkPage], new() { RenameConflictingDestinations = true });
        var navMergedGraph = new PdfGraph(navMerged);
        var navMergedRoot = navMergedGraph.Get(navMergedGraph.Resolve(navMerged.Trailer.Entries!["Root"]), "Outlines")!;
        Check(navMergedGraph.Number(navMergedRoot, "Count") == 2, "outline roots combined");
        using var independentNavigation = NativeTestDocument.Open(PdfWriter.Write(navMerged));
        Check(independentNavigation.NumberOfPages == 2, "independent reader accepts navigation");

        navCatalog.Entries!["PageLabels"] = Dict(("Nums", Array(Num(0), Dict(("S", Name("r")), ("St", Num(4))))));
        navCatalog.Entries["Lang"] = Str(Encoding.ASCII.GetBytes("en-US"));
        var group = NavAdd(Dict(("Type", Name("OCG")), ("Name", Str(Encoding.ASCII.GetBytes("Hidden")))));
        navCatalog.Entries["OCProperties"] = Dict(("OCGs", Array(group)),
            ("D", Dict(("BaseState", Name("OFF")), ("Order", Array(group)))));
        var attachment = NavAdd(Stream(Encoding.UTF8.GetBytes("attachment content")));
        var specification = NavAdd(Dict(("Type", Name("Filespec")), ("F", Str(Encoding.ASCII.GetBytes("readme.txt"))),
            ("EF", Dict(("F", attachment)))));
        navCatalog.Entries["Names"] = Dict(("EmbeddedFiles", Dict(("Names", Array(Str(Encoding.ASCII.GetBytes("readme.txt")), specification)))));
        var featureCopy = PdfReader.Read(PdfWriter.Write(PdfPageEditor.ExtractPages(navigationSource, [2, 1])));
        var featureGraph = new PdfGraph(featureCopy);
        var featureCatalog = featureGraph.Resolve(featureCopy.Trailer.Entries!["Root"]);
        var labels = featureGraph.Get(featureGraph.Get(featureCatalog, "PageLabels")!, "Nums")!.Items!;
        Check(labels[1].Entries!["St"].Number == "5" && labels[3].Entries!["St"].Number == "4", "page labels follow reordered pages");
        var layerProperties = featureGraph.Get(featureCatalog, "OCProperties")!;
        var layerDefaults = featureGraph.Get(layerProperties, "D")!;
        Check(featureGraph.Get(layerDefaults, "OFF")!.Items!.Count == 1, "hidden layer stays hidden");
        Check(ReferenceEquals(featureGraph.Resolve(featureGraph.Get(layerDefaults, "OFF")!.Items![0]),
            featureGraph.Resolve(featureGraph.Get(layerProperties, "OCGs")!.Items![0])), "layer identity preserved");
        var attachmentNames = featureGraph.Get(featureGraph.Get(featureGraph.Get(featureCatalog, "Names")!, "EmbeddedFiles")!, "Names")!.Items!;
        var embedded = featureGraph.Get(featureGraph.Get(attachmentNames[1], "EF")!, "F")!;
        Check(Encoding.UTF8.GetString(Convert.FromBase64String(embedded.Base64!)) == "attachment content", "attachment bytes preserved");
        Check(PdfFormGraph.Text(featureGraph.Get(featureCatalog, "Lang")!) == "en-US", "catalog language retained");
        Reject(() => PdfDocumentMerger.Merge([featureCopy, featureCopy]));
        navCatalog.Entries.Remove("Names");
        var layerMerge = PdfDocumentMerger.Merge([navigationSource, navigationSource], new() { RenameConflictingDestinations = true });
        var layerGraph = new PdfGraph(layerMerge);
        var layerCatalog = layerGraph.Resolve(layerMerge.Trailer.Entries!["Root"]);
        Check(layerGraph.Get(layerGraph.Get(layerCatalog, "OCProperties")!, "OCGs")!.Items!.Count == 2, "merged layer identities remain independent");
        using var independentFeatures = NativeTestDocument.Open(PdfWriter.Write(featureCopy));
        Check(independentFeatures.NumberOfPages == 2, "independent reader accepts page features");

        void RejectLimit(Action action)
        {
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                return;
            }
            throw new Exception("Expected an assembly resource limit failure.");
        }
        var limitedSource = PdfReader.Read(PdfFixture.Create(null, new("Budget"), new("Second")));
        var limitBefore = PdfConverter.ToJson(limitedSource);
        RejectLimit(() => PdfDocumentMerger.Merge([limitedSource], new() { MaxPages = 1 }));
        RejectLimit(() => PdfPageEditor.ExtractPages(limitedSource, [1], new() { MaxObjects = 2 }));
        RejectLimit(() => PdfPageEditor.ExtractPages(limitedSource, [1], new() { MaxNodes = 1 }));
        RejectLimit(() => PdfPageEditor.ExtractPages(limitedSource, [1], new() { MaxEncodedStreamBytes = 0 }));
        Reject(() => PdfPageEditor.ExtractPages(limitedSource, [1], new() { MaxPages = 0 }));
        Check(PdfConverter.ToJson(limitedSource) == limitBefore, "limits preserve source");
        using (var cancellation = new CancellationTokenSource())
        {
            using var enumerator = PdfPageEditor.SplitPages(limitedSource, cancellationToken: cancellation.Token).GetEnumerator();
            Check(enumerator.MoveNext(), "lazy split first page");
            cancellation.Cancel();
            try
            {
                enumerator.MoveNext();
                throw new Exception("Expected cancellation.");
            }
            catch (OperationCanceledException)
            {
            }
        }
        var duplicateForms = PdfDocumentMerger.Merge([firstFormPage, firstFormPage], new() { NamespaceFormFields = true });
        var namespacedFields = PdfFormExtractor.Extract(duplicateForms);
        Check(namespacedFields.Select(field => field.Name).SequenceEqual(new[] { "Document1_Name", "Document2_Name" }), "explicit field namespace policy");
        PdfFormEditor.SetValue(duplicateForms, "Document2_Name", "Independent");
        Check(PdfFormExtractor.Extract(duplicateForms).Single(field => field.Name == "Document1_Name").Values[0] == "Bob", "namespaced fields remain independent");
        var duplicateAttachments = PdfDocumentMerger.Merge([featureCopy, featureCopy], new()
        {
            RenameConflictingAttachments = true,
            RenameConflictingDestinations = true
        });
        var attachmentGraph = new PdfGraph(duplicateAttachments);
        var attachmentCatalog = attachmentGraph.Resolve(duplicateAttachments.Trailer.Entries!["Root"]);
        Check(attachmentGraph.Get(attachmentGraph.Get(attachmentGraph.Get(attachmentCatalog, "Names")!, "EmbeddedFiles")!, "Names")!.Items!.Count == 4, "attachment conflict policy keeps both");
        var destinationNames = attachmentGraph.Get(attachmentGraph.Get(attachmentGraph.Get(attachmentCatalog, "Names")!, "Dests")!, "Names")!.Items!;
        Check(destinationNames.Count == 4, "named destination conflict policy keeps both");
        var sourceCatalog = new PdfGraph(limitedSource).Resolve(limitedSource.Trailer.Entries!["Root"]);
        sourceCatalog.Entries!["UnsupportedExtension"] = Dict();
        Reject(() => PdfPageEditor.ExtractPages(limitedSource, [1]));
        sourceCatalog.Entries.Remove("UnsupportedExtension");
        var sourcePageReference = new PdfGraph(limitedSource).Get(sourceCatalog, "Pages")!.Entries!["Kids"].Items![0];
        sourceCatalog.Entries["OpenAction"] = Array(sourcePageReference, Name("Fit"));
        var opened = PdfPageEditor.ExtractPages(limitedSource, [2, 1]);
        var openedGraph = new PdfGraph(opened);
        var openedCatalog = openedGraph.Resolve(opened.Trailer.Entries!["Root"]);
        Check(ReferenceEquals(openedGraph.Resolve(openedGraph.Get(openedCatalog, "OpenAction")!.Items![0]),
            openedGraph.Pages()[1].Dictionary), "initial view destination remapped");


        var actionsSource = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Actions")));
        PdfFormEditor.InsertTextField(actionsSource, 1, "Value", "Test", 10, 10, 100, 20);
        var actionsGraph = new PdfGraph(actionsSource);
        var actionsForm = new PdfFormGraph(actionsSource);
        actionsForm.Fields[0].Node.Entries!["AA"] = Dict(("K", Dict(("S", Name("ResetForm")),
            ("Fields", Array(Str(Encoding.ASCII.GetBytes("Value")))))));
        var renamedActions = PdfDocumentMerger.Merge([actionsSource], new() { NamespaceFormFields = true });
        var renamedForm = new PdfFormGraph(renamedActions);
        var actionFields = renamedForm.Graph.Get(renamedForm.Graph.Get(renamedForm.Graph.Get(renamedForm.Fields[0].Node, "AA")!, "K")!, "Fields")!;
        Check(PdfFormGraph.Text(actionFields.Items![0]) == "Document1_Value", "field action strings remapped");
        actionsForm.Fields[0].Node.Entries!["AA"] = Dict(("K", Dict(("S", Name("JavaScript")), ("JS", Str(Encoding.ASCII.GetBytes("this.getField('Value')"))))));
        var scriptBefore = PdfConverter.ToJson(actionsSource);
        Reject(() => PdfDocumentMerger.Merge([actionsSource], new() { NamespaceFormFields = true }));
        Check(PdfConverter.ToJson(actionsSource) == scriptBefore, "script rejection leaves source unchanged");

        var intentSource = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Intent")));
        var intentGraph = new PdfGraph(intentSource);
        var intentCatalog = intentGraph.Resolve(intentSource.Trailer.Entries!["Root"]);
        intentCatalog.Entries!["OutputIntents"] = Array(Dict(("Type", Name("OutputIntent")), ("S", Name("GTS_PDFX")),
            ("OutputConditionIdentifier", Str(Encoding.ASCII.GetBytes("Test printing condition")))));
        var matchingIntents = PdfDocumentMerger.Merge([intentSource, intentSource]);
        var matchingGraph = new PdfGraph(matchingIntents);
        Check(matchingGraph.Get(matchingGraph.Resolve(matchingIntents.Trailer.Entries!["Root"]), "OutputIntents")!.Items!.Count == 1, "compatible output intents retained once");
        Reject(() => PdfDocumentMerger.Merge([intentSource, PdfReader.Read(PdfFixture.Create(null, new FixturePage("No intent")))]));
        var differentIntent = PdfConverter.FromJson(PdfConverter.ToJson(intentSource));
        new PdfGraph(differentIntent).Get(new PdfGraph(differentIntent).Resolve(differentIntent.Trailer.Entries!["Root"]), "OutputIntents")!.Items![0].Entries!["OutputConditionIdentifier"] = Str(Encoding.ASCII.GetBytes("Different condition"));
        Reject(() => PdfDocumentMerger.Merge([intentSource, differentIntent]));

        var optionalContent = navGraph.Get(navCatalog, "OCProperties")!;
        var defaultConfig = navGraph.Get(optionalContent, "D")!;
        defaultConfig.Entries!["AS"] = Array(Dict(("Event", Name("View")), ("Category", Array(Name("View")))));
        var automaticLayers = PdfDocumentMerger.Merge([navigationSource, navigationSource], new() { RenameConflictingDestinations = true });
        var autoGraph = new PdfGraph(automaticLayers);
        var autoCatalog = autoGraph.Resolve(automaticLayers.Trailer.Entries!["Root"]);
        var applications = autoGraph.Get(autoGraph.Get(autoGraph.Get(autoCatalog, "OCProperties")!, "D")!, "AS")!.Items!;
        Check(applications.Count == 2 && applications.All(item => item.Entries!["OCGs"].Items!.Count == 1), "automatic layer applications scoped to their own source groups");
        Check(applications[0].Entries!["OCGs"].Items![0].Reference != applications[1].Entries!["OCGs"].Items![0].Reference, "automatic layers do not affect other sources");
        using var independentProduction = NativeTestDocument.Open(PdfWriter.Write(duplicateAttachments));
        Check(independentProduction.NumberOfPages == 4, "independent reader accepts conflict-resolved merge");

        var tagged = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Accessible text")));
        var taggedGraph = new PdfGraph(tagged);
        var taggedCatalog = taggedGraph.Resolve(tagged.Trailer.Entries!["Root"]);
        var taggedPage = taggedGraph.Pages()[0].Dictionary;
        var pageReference = taggedGraph.Get(taggedCatalog, "Pages")!.Entries!["Kids"].Items![0];
        var structureId = new PdfReference(tagged.Objects.Max(item => item.Id.ObjectNumber) + 1);
        var elementId = new PdfReference(structureId.ObjectNumber + 1);
        tagged.Objects.Add(new(structureId, Dict(("Type", Name("StructTreeRoot")), ("K", Array(Ref(elementId))),
            ("ParentTree", Dict(("Nums", Array(Num(0), Array(Ref(elementId)))))))));
        tagged.Objects.Add(new(elementId, Dict(("Type", Name("StructElem")), ("S", Name("P")), ("P", Ref(structureId)),
            ("Pg", pageReference), ("K", Num(0)))));
        taggedCatalog.Entries!["StructTreeRoot"] = Ref(structureId);
        taggedCatalog.Entries["MarkInfo"] = Dict(("Marked", new PdfValue { Type = "boolean", Boolean = true }));
        taggedCatalog.Entries["Metadata"] = Str(Encoding.ASCII.GetBytes("Conformance assertion"));
        taggedPage.Entries!["StructParents"] = Num(0);
        taggedPage.Entries["Tabs"] = Name("S");
        var taggedBefore = PdfConverter.ToJson(tagged);
        var untagged = PdfPageEditor.ExtractPages(tagged, [1], new());
        var untaggedGraph = new PdfGraph(untagged);
        var untaggedCatalog = untaggedGraph.Resolve(untagged.Trailer.Entries!["Root"]);
        Check(untaggedCatalog.Entries!.ContainsKey("StructTreeRoot") && untaggedCatalog.Entries.ContainsKey("MarkInfo")
            && untaggedCatalog.Entries.ContainsKey("Metadata"), "structure and metadata retained");
        Check(untaggedGraph.Pages()[0].Dictionary.Entries!.ContainsKey("StructParents")
            && untaggedGraph.Pages()[0].Dictionary.Entries!.ContainsKey("Tabs"), "structural page pointers and tab ordering retained");
        Check(PdfConverter.ToJson(tagged) == taggedBefore, "structure copying does not modify source");
        using var independentUntagged = NativeTestDocument.Open(PdfWriter.Write(untagged));
        Check(independentUntagged.GetPage(1).Text == "Accessible text", "visible content survives tagged extraction");
        Console.WriteLine("Page extraction, splitting and merging checks passed.");
    }
}
