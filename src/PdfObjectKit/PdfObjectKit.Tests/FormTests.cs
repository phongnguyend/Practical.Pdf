using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class FormTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("AcroForm: " + name);
            }

            checks++;
        }

        var doc = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Form fixture")));
        Check(PdfFormExtractor.Extract(doc).Count == 0, "no form");
        var graph = new PdfGraph(doc);
        var page = graph.Pages()[0].Dictionary;
        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(doc.Objects.Max(o => o.Id.ObjectNumber) + 1);
            doc.Objects.Add(new(id, value));
            return Ref(id);
        }

        PdfValue Text(string text) => Str(Encoding.ASCII.GetBytes(text));
        var annotations = Array();
        page.Entries!["Annots"] = annotations;
        PdfValue Widget()
        {
            var widget = Dict(("Type", Name("Annot")), ("Subtype", Name("Widget")), ("Rect", Array(Num(40), Num(600), Num(250), Num(625))));
            var reference = Add(widget);
            annotations.Items!.Add(reference);
            return reference;
        }

        var textWidget = Widget();
        var field = Add(Dict(("T", Text("FullName")), ("V", Text("Old")), ("MaxLen", Num(30)), ("Kids", Array(textWidget))));
        doc.Objects.Single(o => o.Id == textWidget.Reference).Value.Entries!["Parent"] = field;
        var group = Add(Dict(("T", Text("Applicant")), ("FT", Name("Tx")), ("Ff", Num(2)), ("Kids", Array(field))));
        doc.Objects.Single(o => o.Id == field.Reference).Value.Entries!["Parent"] = group;
        var choice = Widget();
        var choiceNode = doc.Objects.Single(o => o.Id == choice.Reference).Value;
        choiceNode.Entries!["T"] = Text("Country");
        choiceNode.Entries["FT"] = Name("Ch");
        choiceNode.Entries["Ff"] = Num(1 << 17);
        choiceNode.Entries["Opt"] = Array(Array(Text("US"), Text("United States")), Text("Canada"));

        PdfValue Button(string name, string state)
        {
            var widget = Widget();
            var node = doc.Objects.Single(o => o.Id == widget.Reference).Value;
            node.Entries!["T"] = Text(name);
            node.Entries["FT"] = Name("Btn");
            PdfValue Appearance(string content)
            {
                var stream = Stream(Encoding.ASCII.GetBytes(content));
                stream.Entries!["Type"] = Name("XObject");
                stream.Entries["Subtype"] = Name("Form");
                stream.Entries["BBox"] = Array(Num(0), Num(0), Num(20), Num(20));
                return Add(stream);
            }

            node.Entries["AP"] = Dict(("N", Dict(("Off", Appearance("")), (state, Appearance("0 0 m 20 20 l S")))));
            node.Entries["AS"] = Name("Off");
            return widget;
        }

        var check = Button("Accepted", "Yes");
        var radio1 = Button("Remove", "A");
        var radio2 = Button("Remove", "B");
        foreach (var widget in new[] { radio1, radio2 })
        {
            var node = doc.Objects.Single(o => o.Id == widget.Reference).Value;
            node.Entries!.Remove("T");
            node.Entries.Remove("FT");
        }

        var radio = Add(Dict(("T", Text("Plan")), ("FT", Name("Btn")), ("Ff", Num(1 << 15)), ("Kids", Array(radio1, radio2))));
        foreach (var widget in new[] { radio1, radio2 })
        {
            doc.Objects.Single(o => o.Id == widget.Reference).Value.Entries!["Parent"] = radio;
        }

        var acroForm = Dict(("Fields", Array(group, choice, check, radio)));
        graph.Resolve(doc.Trailer.Entries!["Root"]).Entries!["AcroForm"] = Add(acroForm);
        var original = PdfConverter.ToJson(doc);
        var extracted = PdfFormExtractor.Extract(doc);
        Check(extracted.Count == 4, "terminal fields only");
        Check(extracted[0].Name == "Applicant.FullName" && extracted[0].IsRequired && extracted[0].Kind == PdfFormFieldKind.Text, "hierarchy and inherited attributes");
        Check(extracted[0].Widgets.Single().PageNumber == 1 && extracted[0].Values.Single() == "Old", "widget page and value");
        Check(extracted[1].Choices[0] == new PdfFormChoice("US", "United States"), "export/display values");
        Check(PdfConverter.ToJson(doc) == original, "extractor does not mutate");
        PdfFormEditor.SetValue(doc, "Applicant.FullName", "Jane (Smith)");
        PdfFormEditor.SetValue(doc, "Country", "US");
        PdfFormEditor.SetChecked(doc, "Accepted", true);
        PdfFormEditor.SetValue(doc, "Plan", "B");
        extracted = PdfFormExtractor.Extract(doc);
        Check(extracted[0].Values.Single() == "Jane (Smith)", "text value");
        Check(extracted[1].Values.Single() == "US", "choice export value");
        Check(extracted[2].Values.Single() == "Yes" && extracted[3].Values.Single() == "B", "button values");
        graph = new PdfGraph(doc);
        Check(graph.Get(radio1, "AS")?.Name == "Off" && graph.Get(radio2, "AS")?.Name == "B", "radio appearances synchronized");
        var appearance = graph.Get(graph.Get(textWidget, "AP")!, "N")!;
        var syntax = Encoding.ASCII.GetString(Convert.FromBase64String(appearance.Base64!));
        Check(syntax.Contains(Convert.ToHexString(Encoding.ASCII.GetBytes("Jane (Smith)"))), "appearance contains escaped new text");
        var choiceAppearance = graph.Get(graph.Get(choice, "AP")!, "N")!;
        Check(Encoding.ASCII.GetString(Convert.FromBase64String(choiceAppearance.Base64!)).Contains(Convert.ToHexString(Encoding.ASCII.GetBytes("United States"))), "choice displays label");
        var bytes = PdfWriter.Write(doc);
        Check(PdfFormExtractor.Extract(PdfReader.Read(bytes))[0].Values.Single() == "Jane (Smith)", "writer/reader round trip");
        using (var independent = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(bytes), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        {
            Check(independent.AcroForm.Fields.Count == 4, "independent reader sees field tree");
        }

        PdfFormEditor.SetChecked(doc, "Accepted", false);
        Check(PdfFormExtractor.Extract(doc)[2].Values.Single() == "Off", "checkbox cleared");
        foreach (var (name, value) in new[] { ("Missing", "X"), ("Applicant.FullName", "é"), ("Country", "Unknown"), ("Plan", "C"), ("Applicant.FullName", new string('X', 31)) })
        {
            var before = PdfConverter.ToJson(doc);
            var rejected = false;
            try
            {
                PdfFormEditor.SetValue(doc, name, value);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                rejected = true;
            }

            Check(rejected && before == PdfConverter.ToJson(doc), "invalid edit is atomic: " + name);
        }

        var repeated = PdfConverter.FromJson(PdfConverter.ToJson(doc));
        var repeatedField = repeated.Objects.Single(o => o.Id == field.Reference).Value;
        var secondWidgetId = new PdfReference(repeated.Objects.Max(o => o.Id.ObjectNumber) + 1);
        var secondWidget = Dict(("Subtype", Name("Widget")), ("Parent", field), ("Rect", Array(Num(30), Num(40), Num(130), Num(60))));
        repeated.Objects.Add(new(secondWidgetId, secondWidget));
        repeatedField.Entries!["Kids"].Items!.Add(Ref(secondWidgetId));
        PdfFormEditor.SetValue(repeated, "Applicant.FullName", "Both");
        var repeatedGraph = new PdfGraph(repeated);
        Check(new[] { textWidget, Ref(secondWidgetId) }.All(w =>
            Encoding.ASCII.GetString(Convert.FromBase64String(repeatedGraph.Get(repeatedGraph.Get(w, "AP")!, "N")!.Base64!))
                .Contains(Convert.ToHexString(Encoding.ASCII.GetBytes("Both")))), "all field widgets updated");
        PdfFormEditor.SetValue(repeated, "Applicant.FullName", "");
        Check(PdfFormExtractor.Extract(repeated)[0].Values.Single() == "", "empty text clears field");

        foreach (var scenario in new[] { "multiline", "rotated", "xfa", "cycle" })
        {
            var unsupported = PdfConverter.FromJson(PdfConverter.ToJson(doc));
            var unsupportedGraph = new PdfGraph(unsupported);
            switch (scenario)
            {
                case "multiline":
                    unsupportedGraph.Resolve(field).Entries!["Ff"] = Num(1 << 12);
                    break;
                case "rotated":
                    unsupportedGraph.Resolve(textWidget).Entries!["MK"] = Dict(("R", Num(90)));
                    break;
                case "xfa":
                    unsupportedGraph.Get(unsupportedGraph.Resolve(unsupported.Trailer.Entries!["Root"]), "AcroForm")!.Entries!["XFA"] = Text("XFA");
                    break;
                case "cycle":
                    unsupportedGraph.Resolve(group).Entries!["Kids"] = Array(group);
                    break;
            }

            var before = PdfConverter.ToJson(unsupported);
            var rejected = false;
            try
            {
                PdfFormEditor.SetValue(unsupported, "Applicant.FullName", "No");
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
            {
                rejected = true;
            }

            Check(rejected && PdfConverter.ToJson(unsupported) == before, "unsupported edit is atomic: " + scenario);
        }

        var renamed = PdfConverter.FromJson(PdfConverter.ToJson(doc));
        var renameBefore = PdfConverter.ToJson(renamed);
        var previousTitle = renamed.Objects.Single(o => o.Id == field.Reference).Value.Entries!["T"];
        PdfFormEditor.RenameField(renamed, "Applicant.FullName", "LegalName\u00e9");
        var renamedField = PdfFormExtractor.Extract(PdfReader.Read(PdfWriter.Write(renamed)))[0];
        Check(renamedField.Name == "Applicant.LegalName\u00e9" && renamedField.Values.Single() == "Jane (Smith)", "rename preserves parent/value and Unicode name round trip");
        renamed.Objects.Single(o => o.Id == field.Reference).Value.Entries!["T"] = previousTitle;
        Check(PdfConverter.ToJson(renamed) == renameBefore, "rename changes only T; widgets and appearances preserved");
        PdfFormEditor.RenameField(renamed, "Country", "Location");
        PdfFormEditor.SetValue(renamed, "Location", "Canada");
        Check(PdfFormExtractor.Extract(renamed).Single(f => f.Name == "Location").Values.Single() == "Canada", "renamed field remains editable");
        foreach (var (oldName, newName) in new[] { ("Country", "Accepted"), ("Country", "Applicant"), ("Country", " "), ("Country", "A.B"), ("Missing", "New") })
        {
            var before = PdfConverter.ToJson(doc);
            var rejected = false;
            try
            {
                PdfFormEditor.RenameField(doc, oldName, newName);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            Check(rejected && PdfConverter.ToJson(doc) == before, "invalid rename is atomic: " + newName);
        }

        var scripted = PdfConverter.FromJson(PdfConverter.ToJson(doc));
        new PdfGraph(scripted).Resolve(scripted.Trailer.Entries!["Root"]).Entries!["OpenAction"] = Dict(("S", Name("JavaScript")), ("JS", Text("this.getField('Country');")));
        var scriptedBefore = PdfConverter.ToJson(scripted);
        try
        {
            PdfFormEditor.RenameField(scripted, "Country", "Location");
            throw new Exception("Scripted form rename accepted");
        }
        catch (NotSupportedException)
        {
            Check(scriptedBefore == PdfConverter.ToJson(scripted), "scripted rename rejected atomically");
        }

        var currentField = doc.Objects.Single(o => o.Id == field.Reference).Value;
        currentField.Entries!["Ff"] = Num(1);
        try
        {
            PdfFormEditor.SetValue(doc, "Applicant.FullName", "No");
            throw new Exception("Read-only accepted");
        }
        catch (InvalidOperationException)
        {
            checks++;
        }

        try
        {
            PdfFormEditor.RenameField(doc, "Applicant.FullName", "LegalName");
            throw new Exception("Read-only rename accepted");
        }
        catch (InvalidOperationException)
        {
            checks++;
        }

        var aligned = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Alignment")));
        foreach (var alignment in Enum.GetValues<PdfTextAlignment>())
        {
            var name = "Aligned" + alignment;
            PdfFormEditor.InsertTextField(aligned, 1, name, "A", 40, 40, 200, 30, alignment);
            var alignedModel = PdfFormExtractor.Extract(aligned).Single(f => f.Name == name);
            Check(alignedModel.TextAlignment == alignment, "extract " + alignment);
            var alignmentGraph = new PdfFormGraph(aligned);
            var alignedField = alignmentGraph.Fields.Single(f => f.Model.Name == name);
            var alignedAppearance = alignmentGraph.Graph.Get(alignmentGraph.Graph.Get(alignedField.Widgets[0], "AP")!, "N")!;
            var content = Encoding.ASCII.GetString(Convert.FromBase64String(alignedAppearance.Base64!));
            var expectedX = alignment switch
            {
                PdfTextAlignment.Center => (200 - 8.004) / 2,
                PdfTextAlignment.Right => 200 - 2 - 8.004,
                _ => 2
            };
            Check(content.Contains($"1 0 0 1 {Syntax(Num(expectedX))} 11 Tm"), "appearance position " + alignment);
            PdfFormEditor.SetValue(aligned, name, "AB");
            Check(PdfFormExtractor.Extract(aligned).Single(f => f.Name == name).TextAlignment == alignment, "preserve alignment on edit");
        }

        PdfFormEditor.SetValue(aligned, "AlignedLeft", "AB", PdfTextAlignment.Right);
        Check(PdfFormExtractor.Extract(PdfReader.Read(PdfWriter.Write(aligned))).Single(f => f.Name == "AlignedLeft").TextAlignment == PdfTextAlignment.Right, "alignment survives rebuild");
        var beforeInvalidAlignment = PdfConverter.ToJson(aligned);
        try
        {
            PdfFormEditor.SetValue(aligned, "AlignedLeft", "Bad", (PdfTextAlignment)99);
            throw new Exception("Invalid alignment accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
            Check(PdfConverter.ToJson(aligned) == beforeInvalidAlignment, "invalid alignment is atomic");
        }

        Console.WriteLine($"Passed {checks} AcroForm checks.");
    }
}
