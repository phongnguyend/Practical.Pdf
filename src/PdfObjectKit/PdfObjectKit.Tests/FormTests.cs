using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Formats.Asn1;
using PdfObjectKit.Signing;
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

        foreach (var scenario in new[] { "richText", "rotated", "xfa", "cycle" })
        {
            var unsupported = PdfConverter.FromJson(PdfConverter.ToJson(doc));
            var unsupportedGraph = new PdfGraph(unsupported);
            switch (scenario)
            {
                case "richText":
                    unsupportedGraph.Resolve(field).Entries!["Ff"] = Num(1 << 25);
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

        TestFieldCreation();
        TestMultilineAndLists();
        TestMultiSelectLists();
        TestPushButtonsAndSignatures();
        TestPasswordAndComb();
        TestRemainingFeatures();
        Console.WriteLine($"Passed {checks} AcroForm checks.");
    }

    private static void TestRemainingFeatures()
    {
        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage()));
        PdfFormEditor.InsertTextField(document, 1, "File", "report.txt", 10, 10, 180, 30, fileSelect: true);
        const string rich = "<body><p>Normal <b>bold</b> <span style=\"color:#ff0000;font-size:18pt\"><i>red</i></span><br/><u>under</u></p></body>";
        PdfFormEditor.InsertRichTextField(document, 1, "Rich", rich, 10, 50, 250, 90);
        var fields = PdfFormExtractor.Extract(PdfReader.Read(PdfWriter.Write(document)));
        if ((fields.Single(f => f.Name == "File").Flags & (1 << 20)) == 0
            || fields.Single(f => f.Name == "Rich").Values.Single() != "Normal bold red\nunder"
            || !fields.Single(f => f.Name == "Rich").RichText!.Contains("font-size:18pt"))
        {
            throw new Exception("File-select and rich-text metadata must round-trip.");
        }
        var rendered = PdfObjectKit.Rendering.PdfCanvasExporter.Export(document, 1);
        if (!rendered.Commands.Any(c => c.FontName == "Helvetica-Bold") || !rendered.Commands.Any(c => c.Kind == "rg" && c.Numbers.SequenceEqual(new double[] { 1, 0, 0 })))
        {
            throw new Exception("Rich appearances must preserve fonts and colors.");
        }
        void Reject(Action action)
        {
            var before = PdfConverter.ToJson(document);
            try
            {
                action();
                throw new Exception("Invalid remaining-feature operation accepted.");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Xml.XmlException)
            {
                if (before != PdfConverter.ToJson(document))
                {
                    throw new Exception("Rejected feature operation must be atomic.");
                }
            }
        }
        Reject(() => PdfFormEditor.SetRichText(document, "Rich", "<!DOCTYPE body [<!ENTITY x SYSTEM 'file:///missing'>]><body>&x;</body>"));
        Reject(() => PdfFormEditor.SetRichText(document, "Rich", "<body><script>bad</script></body>"));
        Reject(() => PdfFormEditor.SetRichText(document, "Rich", "<body style='background:url(test)'>bad</body>"));
        PdfFormEditor.SetRichText(document, "Rich", "<body><p><b>Updated</b></p></body>");
        PdfFormEditor.InsertPushButton(document, 1, "Button", "Action", 10, 150, 150, 30);
        foreach (var action in new[] {
            new PdfButtonAction(PdfButtonActionKind.OpenUrl) { Url = "https://example.com/" },
            new PdfButtonAction(PdfButtonActionKind.GoToPage) { PageNumber = 1 },
            new PdfButtonAction(PdfButtonActionKind.ResetForm) { FieldNames = ["File"] },
            new PdfButtonAction(PdfButtonActionKind.SubmitForm) { Url = "https://example.com/submit", FieldNames = ["Rich"] } })
        {
            PdfFormEditor.SetButtonAction(document, "Button", action);
            if (PdfFormExtractor.Extract(PdfReader.Read(PdfWriter.Write(document))).Single(f => f.Name == "Button").Widgets[0].Action?.Kind != action.Kind)
            {
                throw new Exception("Button action must round-trip.");
            }
        }
        Reject(() => PdfFormEditor.SetButtonAction(document, "Button", new(PdfButtonActionKind.OpenUrl) { Url = "javascript:bad()" }));
        Reject(() => PdfFormEditor.SetButtonAction(document, "Button", new(PdfButtonActionKind.GoToPage) { PageNumber = 99 }));
        PdfFormEditor.SetButtonAction(document, "Button", null);
        PdfFormEditor.InsertSignatureField(document, 1, "Sign", 10, 190, 180, 40);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=PdfObjectKit test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        if (Environment.GetEnvironmentVariable("PDFOBJECTKIT_TEST_CERTIFICATE_PATH") is { Length: > 0 } certificatePath)
        {
            // Only a freshly generated self-signed test identity is exported for the browser signing test.
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pkcs12, "test-only"));
        }
        var beforeSigning = PdfConverter.ToJson(document);
        var signed = PdfSigner.Sign(document, "Sign", certificate, "Test signature");
        if (PdfConverter.ToJson(document) != beforeSigning)
        {
            throw new Exception("Signing must not mutate the input document.");
        }
        var signedDocument = PdfReader.Read(signed);
        var form = new PdfFormGraph(signedDocument);
        var signature = form.Graph.Get(form.Fields.Single(f => f.Model.Name == "Sign").Node, "V")!;
        var range = form.Graph.Get(signature, "ByteRange")!.Items!.Select(v => checked((int)Number(v))).ToArray();
        var data = signed.AsSpan(range[0], range[1]).ToArray().Concat(signed.AsSpan(range[2], range[3]).ToArray()).ToArray();
        var padded = Convert.FromBase64String(form.Graph.Get(signature, "Contents")!.Base64!);
        var cmsBytes = new AsnReader(padded, AsnEncodingRules.DER).ReadEncodedValue().ToArray();
        var cms = new SignedCms(new ContentInfo(data), detached: true);
        cms.Decode(cmsBytes);
        cms.CheckSignature(verifySignatureOnly: true);
        if (range[0] != 0 || range[2] + range[3] != signed.Length || cms.SignerInfos.Count != 1
            || !form.Fields.Single(f => f.Model.Name == "Sign").Model.IsSigned)
        {
            throw new Exception("Signature must cover the entire PDF except its Contents and be extractable.");
        }
        data[0] ^= 1;
        var tampered = new SignedCms(new ContentInfo(data), detached: true);
        tampered.Decode(cmsBytes);
        try
        {
            tampered.CheckSignature(verifySignatureOnly: true);
            throw new Exception("Tampered PDF signature was accepted.");
        }
        catch (CryptographicException)
        {
            // Independent CMS verification must detect a changed signed byte.
        }
        try
        {
            PdfSigner.Sign(signedDocument, "Sign", certificate);
            throw new Exception("A second signature was accepted without incremental writing.");
        }
        catch (NotSupportedException)
        {
            // Existing signatures must not be invalidated by a rewrite.
        }
        Console.WriteLine("Rich-text, file-select, button action and cryptographic signature checks passed.");
    }

    private static void TestPasswordAndComb()
    {
        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage()));
        PdfFormEditor.InsertTextField(document, 1, "Password", "Secret", 40, 40, 150, 30, maxLength: 8, password: true);
        PdfFormEditor.InsertTextField(document, 1, "Code", "AB", 40, 80, 120, 30, maxLength: 6, comb: true);
        document = PdfReader.Read(PdfWriter.Write(document));
        var form = new PdfFormGraph(document);
        var password = form.Fields.Single(f => f.Model.Name == "Password");
        string Appearance(PdfFormGraph graph, PdfFormGraph.Field field) => Encoding.ASCII.GetString(Convert.FromBase64String(graph.Graph.Get(graph.Graph.Get(field.Widgets[0], "AP")!, "N")!.Base64!));
        if (password.Model.MaxLength != 8 || password.Model.Values.Single() != "Secret" || (password.Model.Flags & (1 << 13)) == 0
            || Appearance(form, password).Contains("Secret") || !Appearance(form, password).Contains(Syntax(Str(Encoding.ASCII.GetBytes("******")))))
        {
            throw new Exception("Password appearance must be masked while preserving the stored value and character limit.");
        }
        var rendered = string.Concat(PdfObjectKit.Rendering.PdfCanvasExporter.Export(document, 1).Commands.SelectMany(c => c.Glyphs ?? []).Select(g => g.Text));
        if (rendered.Contains("Secret") || !rendered.Contains("******"))
        {
            throw new Exception("Native rendering must use the masked password appearance.");
        }
        foreach (var alignment in Enum.GetValues<PdfTextAlignment>())
        {
            PdfFormEditor.SetValue(document, "Code", "AB", alignment);
            var commands = PdfObjectKit.Rendering.PdfCanvasExporter.Export(document, 1).Commands.Where(c => c.Kind == "text" && c.Glyphs!.Count == 1 && c.Glyphs[0].Text is "A" or "B").ToList();
            var firstCell = alignment == PdfTextAlignment.Right ? 4 : alignment == PdfTextAlignment.Center ? 2 : 0;
            if (commands.Count != 2 || Math.Abs(commands[0].Numbers[4] - (40 + firstCell * 20 + (20 - 8.004) / 2)) > .001
                || Math.Abs(commands[1].Numbers[4] - commands[0].Numbers[4] - 20) > .001)
            {
                throw new Exception("Comb glyphs must be centered in separate cells with the selected alignment.");
            }
        }
        void Reject(Action action)
        {
            var before = PdfConverter.ToJson(document);
            try
            {
                action();
                throw new Exception("Invalid text mode accepted.");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidDataException)
            {
                if (before != PdfConverter.ToJson(document))
                {
                    throw new Exception("Invalid text mode operations must be atomic.");
                }
            }
        }
        Reject(() => PdfFormEditor.SetValue(document, "Code", "1234567"));
        Reject(() => PdfFormEditor.SetValue(document, "Password", "123456789"));
        Reject(() => PdfFormEditor.SetValue(document, "Password", "a\nb"));
        Reject(() => PdfFormEditor.InsertTextField(document, 1, "Invalid", "", 0, 0, 100, 30, comb: true));
        Reject(() => PdfFormEditor.InsertTextField(document, 1, "Invalid", "", 0, 0, 100, 30, maxLength: 0));
        Reject(() => PdfFormEditor.InsertTextField(document, 1, "Invalid", "", 0, 0, 100, 30, maxLength: 6, password: true, comb: true));
        Reject(() => PdfFormEditor.InsertTextField(document, 1, "Invalid", "", 0, 0, 100, 30, multiline: true, maxLength: 6, comb: true));
        PdfFormEditor.SetValue(document, "Password", "");
        PdfFormEditor.SetValue(document, "Code", "");
        form = new PdfFormGraph(PdfReader.Read(PdfWriter.Write(document)));
        if (form.Fields.Any(f => f.Model.Values.Single() != ""))
        {
            throw new Exception("Password and comb fields must support clearing.");
        }
        Console.WriteLine("Password and comb checks passed.");
    }

    private static void TestPushButtonsAndSignatures()
    {
        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage()));
        PdfFormEditor.InsertPushButton(document, 1, "Continue", "Continue", 20, 40, 120, 30);
        PdfFormEditor.InsertSignatureField(document, 1, "Approval", 20, 90, 180, 50);
        document = PdfReader.Read(PdfWriter.Write(document));
        var form = new PdfFormGraph(document);
        var button = form.Fields.Single(f => f.Model.Name == "Continue");
        var signature = form.Fields.Single(f => f.Model.Name == "Approval");
        if (button.Model.Kind != PdfFormFieldKind.PushButton || button.Model.Widgets.Single().Caption != "Continue"
            || signature.Model.Kind != PdfFormFieldKind.Signature || signature.Inherited.ContainsKey("V")
            || form.Graph.Get(signature.Node, "DV") is not null || ((int)Number(form.Graph.Get(form.AcroForm!, "SigFlags")!) & 1) == 0)
        {
            throw new Exception("Pushbutton caption and unsigned signature fields must round-trip.");
        }
        foreach (var field in form.Fields)
        {
            var widget = field.Widgets.Single();
            if (field.Model.Values.Count != 0 || form.Graph.Get(widget, "A") is not null || form.Graph.Get(widget, "AA") is not null
                || form.Graph.Get(form.Graph.Get(widget, "AP")!, "N")?.Type != "stream" || field.Model.Widgets.Single().PageNumber != 1)
            {
                throw new Exception("New appearance fields must have an appearance and no value or actions.");
            }
        }
        PdfFormEditor.SetPushButtonCaption(document, "Continue", "Next (step)");
        form = new PdfFormGraph(document);
        button = form.Fields.Single(f => f.Model.Name == "Continue");
        var content = Encoding.ASCII.GetString(Convert.FromBase64String(form.Graph.Get(form.Graph.Get(button.Widgets.Single(), "AP")!, "N")!.Base64!));
        if (button.Model.Widgets.Single().Caption != "Next (step)" || !content.Contains(Syntax(Str(Encoding.ASCII.GetBytes("Next (step)"))))
            || button.Model.Values.Count != 0)
        {
            throw new Exception("Caption edits must update metadata and appearance without adding a field value.");
        }
        void Reject(Action action)
        {
            var before = PdfConverter.ToJson(document);
            try
            {
                action();
                throw new Exception("Invalid appearance field operation accepted.");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                if (PdfConverter.ToJson(document) != before)
                {
                    throw new Exception("Rejected appearance field operations must be atomic.");
                }
            }
        }
        Reject(() => PdfFormEditor.InsertPushButton(document, 1, "Continue", "Duplicate", 0, 0, 100, 30));
        Reject(() => PdfFormEditor.InsertSignatureField(document, 0, "Invalid", 0, 0, 100, 30));
        Reject(() => PdfFormEditor.InsertSignatureField(document, 1, "Invalid", 0, 0, -1, 30));
        Reject(() => PdfFormEditor.SetPushButtonCaption(document, "Continue", "Bad\ncaption"));
        Reject(() => PdfFormEditor.SetPushButtonCaption(document, "Approval", "Not a button"));
        Reject(() => PdfFormEditor.SetValue(document, "Approval", "Not a digital signature"));
        Reject(() => PdfFormEditor.SetValue(document, "Continue", "Not a button value"));
        PdfFormEditor.MoveWidget(document, "Approval", 0, 5, 10);
        PdfFormEditor.RenameField(document, "Approval", "ApproverSignature");
        var moved = PdfFormExtractor.Extract(PdfReader.Read(PdfWriter.Write(document))).Single(f => f.Name == "ApproverSignature");
        if (!moved.Widgets.Single().Rectangle.SequenceEqual(new double[] { 25, 100, 205, 150 }))
        {
            throw new Exception("Unsigned signature fields must support moving and renaming.");
        }
        // Simulate a signed field to exercise the existing signed-form mutation guard.
        var signedForm = new PdfFormGraph(document);
        var signedNode = signedForm.Fields.Single(f => f.Model.Name == "ApproverSignature").Node;
        signedNode.Entries!["V"] = Dict(("Type", Name("Sig")));
        Reject(() => PdfFormEditor.InsertSignatureField(document, 1, "Another", 0, 0, 100, 30));
        Reject(() => PdfFormEditor.InsertPushButton(document, 1, "Another", "Button", 0, 0, 100, 30));
        Reject(() => PdfFormEditor.SetPushButtonCaption(document, "Continue", "Blocked"));
        signedNode.Entries.Remove("V");
        PdfFormEditor.DeleteField(document, "ApproverSignature");
        PdfFormEditor.DeleteField(document, "Continue");
        if (PdfFormExtractor.Extract(document).Count != 0)
        {
            throw new Exception("Appearance fields must be removable.");
        }
        Console.WriteLine("Pushbutton and unsigned signature checks passed.");
    }

    private static void TestMultiSelectLists()
    {
        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage()));
        PdfFormEditor.InsertMultiSelectListBox(document, 1, "Languages",
            [new("en", "English"), new("fr", "French"), new("de", "German")], ["de", "en"], 40, 40, 180, 80);
        document = PdfReader.Read(PdfWriter.Write(document));
        var form = new PdfFormGraph(document);
        var field = form.Fields.Single(f => f.Model.Name == "Languages");
        string Appearance(PdfFormGraph graph, PdfFormGraph.Field target)
        {
            return Encoding.ASCII.GetString(Convert.FromBase64String(graph.Graph.Get(graph.Graph.Get(target.Widgets[0], "AP")!, "N")!.Base64!));
        }
        if ((field.Model.Flags & (1 << 21)) == 0 || !field.Model.Values.SequenceEqual(["en", "de"])
            || !form.Graph.Get(field.Node, "I")!.Items!.Select(Number).SequenceEqual(new double[] { 0, 2 })
            || Appearance(form, field).Split("0.2 0.4 0.8 rg").Length != 3)
        {
            throw new Exception("Multi-select values, sorted indices and both highlights must round-trip.");
        }
        PdfFormEditor.SetValues(document, "Languages", ["fr"], PdfTextAlignment.Center);
        form = new PdfFormGraph(document);
        field = form.Fields.Single(f => f.Model.Name == "Languages");
        if (!field.Model.Values.SequenceEqual(["fr"]) || field.Model.TextAlignment != PdfTextAlignment.Center
            || Number(form.Graph.Get(field.Node, "TI")!) != 1 || Number(form.Graph.Get(field.Node, "I")!.Items![0]) != 1)
        {
            throw new Exception("Replacing a selection must update values, alignment and scroll position.");
        }
        void Reject(Action action)
        {
            var before = PdfConverter.ToJson(document);
            try
            {
                action();
                throw new Exception("Invalid multi-select edit accepted.");
            }
            catch (ArgumentException)
            {
                if (PdfConverter.ToJson(document) != before)
                {
                    throw new Exception("Invalid multi-select edits must be atomic.");
                }
            }
        }
        Reject(() => PdfFormEditor.SetValues(document, "Languages", ["missing"]));
        Reject(() => PdfFormEditor.SetValues(document, "Languages", ["en", "en"]));
        Reject(() => PdfFormEditor.SetValues(document, "Languages", [null!]));
        Reject(() => PdfFormEditor.SetValue(document, "Languages", "en"));
        Reject(() => PdfFormEditor.InsertMultiSelectListBox(document, 1, "Invalid", [new("en", "English")], ["missing"], 0, 0, 100, 40));
        PdfFormEditor.InsertTextField(document, 1, "Text", "", 0, 0, 100, 30);
        PdfFormEditor.InsertListBox(document, 1, "Single", [new("en", "English")], "en", 0, 0, 100, 40);
        Reject(() => PdfFormEditor.SetValues(document, "Text", []));
        Reject(() => PdfFormEditor.SetValues(document, "Single", ["en"]));
        PdfFormEditor.SetValues(document, "Languages", []);
        document = PdfReader.Read(PdfWriter.Write(document));
        form = new PdfFormGraph(document);
        field = form.Fields.Single(f => f.Model.Name == "Languages");
        if (field.Model.Values.Count != 0 || form.Graph.Get(field.Node, "I")!.Items!.Count != 0
            || Appearance(form, field).Contains("0.2 0.4 0.8 rg"))
        {
            throw new Exception("Clearing selection must remove all values, indices and highlights.");
        }
        Console.WriteLine("Multi-select list box checks passed.");
    }

    private static void TestMultilineAndLists()
    {
        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage()));
        PdfFormEditor.InsertTextField(document, 1, "Notes", "First\r\nSecond", 10, 10, 100, 80, multiline: true);
        PdfFormEditor.InsertListBox(document, 1, "CountryList", [new("US", "United States"), new("UK", "United Kingdom"), new("FR", "France")], "UK", 120, 10, 120, 60);
        document = PdfReader.Read(PdfWriter.Write(document));
        string Appearance(PdfFormGraph graph, PdfFormGraph.Field field)
        {
            var appearance = graph.Graph.Get(graph.Graph.Get(field.Widgets[0], "AP")!, "N")!;
            return Encoding.ASCII.GetString(Convert.FromBase64String(appearance.Base64!));
        }
        var graph = new PdfFormGraph(document);
        var notes = graph.Fields.Single(f => f.Model.Name == "Notes");
        var list = graph.Fields.Single(f => f.Model.Name == "CountryList");
        if ((notes.Model.Flags & (1 << 12)) == 0 || notes.Model.Values.Single() != "First\r\nSecond"
            || !Appearance(graph, notes).Contains("2 66 Tm") || !Appearance(graph, notes).Contains("2 52 Tm")
            || (list.Model.Flags & (1 << 17)) != 0 || Number(graph.Graph.Get(list.Node, "I")!.Items![0]) != 1
            || Number(graph.Graph.Get(list.Node, "TI")!) != 1 || !Appearance(graph, list).Contains("0.2 0.4 0.8 rg")
            || !Appearance(graph, list).Contains(Syntax(Str(Encoding.ASCII.GetBytes("United Kingdom")))))
        {
            throw new Exception("Multiline and list box flags, values, selection and appearances must round-trip.");
        }
        PdfFormEditor.SetValue(document, "Notes", new string('W', 30), PdfTextAlignment.Right);
        graph = new PdfFormGraph(document);
        notes = graph.Fields.Single(f => f.Model.Name == "Notes");
        if (Appearance(graph, notes).Split(" Tj").Length < 4 || notes.Model.Values.Single().Length != 30)
        {
            throw new Exception("Long words must wrap without losing stored text.");
        }
        PdfFormEditor.SetValue(document, "CountryList", "FR");
        graph = new PdfFormGraph(document);
        list = graph.Fields.Single(f => f.Model.Name == "CountryList");
        if (Number(graph.Graph.Get(list.Node, "I")!.Items![0]) != 2 || list.Model.Values.Single() != "FR")
        {
            throw new Exception("List edits must update selection indices and value.");
        }
        var before = PdfConverter.ToJson(document);
        try
        {
            PdfFormEditor.SetValue(document, "CountryList", "missing");
            throw new Exception("Unknown option accepted.");
        }
        catch (ArgumentException)
        {
            if (before != PdfConverter.ToJson(document))
            {
                throw new Exception("Invalid list edit must be atomic.");
            }
        }
        PdfFormEditor.SetValue(document, "CountryList", "");
        graph = new PdfFormGraph(document);
        list = graph.Fields.Single(f => f.Model.Name == "CountryList");
        if (graph.Graph.Get(list.Node, "I")!.Items!.Count != 0 || Appearance(graph, list).Contains("0.2 0.4 0.8 rg"))
        {
            throw new Exception("Clearing a list must remove its selection and highlight.");
        }
        Console.WriteLine("Multiline text and list box checks passed.");
    }

    private static void TestFieldCreation()
    {
        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage()));
        PdfFormEditor.InsertCheckBox(document, 1, "Agree", true, 20, 20, 20, 20);
        PdfFormEditor.InsertDropdown(document, 1, "Country", [new("US", "United States"), new("UK", "United Kingdom")], "UK", 50, 50, 160, 24);
        PdfFormEditor.InsertRadioGroup(document, 1, "Contact", [new("Email", "By email"), new("Phone", "By phone")], "Email", 50, 100, 160, 48);
        var restored = PdfReader.Read(PdfWriter.Write(document));
        var fields = PdfFormExtractor.Extract(restored);
        if (fields.Count != 3 || fields[0].Kind != PdfFormFieldKind.CheckBox || fields[0].Values.Single() != "Yes"
            || fields[1].Kind != PdfFormFieldKind.Choice || fields[1].Choices[1].DisplayValue != "United Kingdom"
            || fields[2].Kind != PdfFormFieldKind.RadioButton || fields[2].Widgets.Count != 2)
        {
            throw new Exception("New field types must round-trip with options, values and widgets.");
        }
        PdfFormEditor.SetChecked(restored, "Agree", false);
        PdfFormEditor.SetValue(restored, "Country", "US");
        PdfFormEditor.SetValue(restored, "Contact", "Phone");
        var graph = new PdfFormGraph(restored);
        var radio = graph.Fields.Single(f => f.Model.Name == "Contact");
        if (graph.Graph.Get(radio.Widgets[0], "AS")?.Name != "Off" || graph.Graph.Get(radio.Widgets[1], "AS")?.Name != "Phone"
            || graph.Fields.Single(f => f.Model.Name == "Agree").Model.Values.Single() != "Off")
        {
            throw new Exception("Button edits must keep value and widget appearances synchronized.");
        }
        foreach (var field in graph.Fields)
        {
            foreach (var widget in field.Widgets)
            {
                if (graph.Graph.Get(widget, "AP") is null)
                {
                    throw new Exception("Every created widget must have an appearance.");
                }
            }
        }
        void Reject(Action action)
        {
            var before = PdfConverter.ToJson(restored);
            try
            {
                action();
                throw new Exception("Expected invalid form creation to fail.");
            }
            catch (ArgumentException)
            {
                if (PdfConverter.ToJson(restored) != before)
                {
                    throw new Exception("Rejected field creation must be atomic.");
                }
            }
        }
        Reject(() => PdfFormEditor.InsertCheckBox(restored, 1, "Agree", false, 0, 0, 20, 20));
        Reject(() => PdfFormEditor.InsertCheckBox(restored, 0, "New", false, 0, 0, 20, 20));
        Reject(() => PdfFormEditor.InsertCheckBox(restored, 1, "New", false, 0, 0, 20, 20, "Off"));
        Reject(() => PdfFormEditor.InsertDropdown(restored, 1, "New", [new("a", "A"), new("a", "B")], "a", 0, 0, 50, 20));
        Reject(() => PdfFormEditor.InsertRadioGroup(restored, 1, "New", [new("a", "A"), new("b", "B")], "missing", 0, 0, 80, 40));
        PdfFormEditor.DeleteField(restored, "Contact");
        if (PdfFormExtractor.Extract(restored).Any(field => field.Name == "Contact"))
        {
            throw new Exception("Deleting a radio group must remove all widgets.");
        }
        Console.WriteLine("Form creation checks passed.");
    }
}
