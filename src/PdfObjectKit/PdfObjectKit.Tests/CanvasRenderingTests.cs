using System.Text;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Rendering;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class CanvasRenderingTests
{
    public static void Run()
    {
        var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
        var page = new PdfGraph(document).Pages()[0];
        var form = Stream(Encoding.ASCII.GetBytes("BT /F1 10 Tf 2 Tc 20 30 Td [(A) -100 (B)] TJ ET"));
        form.Entries!["Subtype"] = Name("Form");
        form.Entries["BBox"] = Array(Num(0), Num(0), Num(100), Num(100));
        form.Entries["Resources"] = page.Resources;
        var id = new PdfReference(document.Objects.Max(item => item.Id.ObjectNumber) + 1);
        document.Objects.Add(new(id, form));
        page.Dictionary.Entries!["Resources"] = Dict(("XObject", Dict(("Fm", Ref(id)))));
        page.Streams[0].Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("/Fm Do q 1 0 0 1 100 200 cm /Fm Do Q /Unknown sh"));
        var display = PdfCanvasExporter.Export(document, 1);
        var text = display.Commands.Where(command => command.Kind == "text").ToList();
        if (text.Count != 4 || string.Concat(text.SelectMany(command => command.Glyphs!).Select(glyph => glyph.Text)) != "ABAB")
        {
            throw new Exception("Display list must include every repeated Form text occurrence.");
        }
        if (text[0].Numbers[4] != 20 || text[0].Numbers[5] != 30 || text[2].Numbers[4] != 120 || text[2].Numbers[5] != 230)
        {
            throw new Exception("Display-list text must carry absolute placement matrices.");
        }
        var nextX = 20 + text[0].Glyphs![0].Advance + 1;
        if (Math.Abs(text[1].Numbers[4] - nextX) > .0001 || !display.Warnings.Any(warning => warning.Contains("sh")))
        {
            throw new Exception("Preserve TJ spacing and report unsupported graphics.");
        }
        try
        {
            PdfCanvasExporter.Export(document, 0);
            throw new Exception("Invalid page must be rejected.");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Expected: pages are one-based.
        }
        var maskedDocument = PdfReader.Read(PdfFixture.Create(null, new FixturePage()));
        var maskedPage = new PdfGraph(maskedDocument).Pages()[0];
        var mask = Stream([128]);
        mask.Entries!["Width"] = Num(1);
        mask.Entries["Height"] = Num(1);
        mask.Entries["BitsPerComponent"] = Num(8);
        mask.Entries["ColorSpace"] = Name("DeviceGray");
        mask.Entries["Matte"] = Array(Num(0), Num(0), Num(0));
        var image = Stream([64, 0, 0]);
        image.Entries!["Subtype"] = Name("Image");
        image.Entries["Width"] = Num(1);
        image.Entries["Height"] = Num(1);
        image.Entries["BitsPerComponent"] = Num(8);
        image.Entries["ColorSpace"] = Name("DeviceRGB");
        image.Entries["SMask"] = mask;
        maskedPage.Resources.Entries!["XObject"] = Dict(("Im", image));
        maskedPage.Streams[0].Value.Base64 = Convert.ToBase64String("/Im Do"u8);
        var pixels = Convert.FromBase64String(PdfCanvasExporter.Export(maskedDocument, 1).Commands.Single(c => c.Kind == "rgba").Data!);
        if (!pixels.SequenceEqual(new byte[] { 128, 0, 0, 128 }))
        {
            throw new Exception("Raw images must unblend matte before applying transparency.");
        }
        mask.Entries["Decode"] = Array(Num(1), Num(0));
        pixels = Convert.FromBase64String(PdfCanvasExporter.Export(maskedDocument, 1).Commands.Single(c => c.Kind == "rgba").Data!);
        if (pixels[3] != 127)
        {
            throw new Exception("Soft-mask Decode must control transparency.");
        }
        mask.Base64 = "";
        var truncated = PdfCanvasExporter.Export(maskedDocument, 1);
        if (truncated.Commands.Any(c => c.Kind == "rgba") || !truncated.Warnings.Contains("Truncated alpha samples."))
        {
            throw new Exception("Malformed masks must be reported instead of rendering corrupt image data.");
        }
        var red = Dict(("FunctionType", Num(2)), ("Domain", Array(Num(0), Num(1))),
            ("C0", Array(Num(1), Num(0), Num(0))), ("C1", Array(Num(1), Num(0), Num(0))), ("N", Num(1)));
        var blue = Dict(("FunctionType", Num(2)), ("Domain", Array(Num(0), Num(1))),
            ("C0", Array(Num(0), Num(0), Num(1))), ("C1", Array(Num(0), Num(0), Num(1))), ("N", Num(1)));
        var function = Dict(("FunctionType", Num(3)), ("Domain", Array(Num(0), Num(1))),
            ("Functions", Array(red, blue)), ("Bounds", Array(Num(.5))), ("Encode", Array(Num(0), Num(1), Num(0), Num(1))));
        var shading = Dict(("ShadingType", Num(2)), ("ColorSpace", Name("DeviceRGB")),
            ("Coords", Array(Num(0), Num(0), Num(100), Num(0))), ("Extend", Array(new PdfValue { Type = "boolean", Boolean = true }, new PdfValue { Type = "boolean", Boolean = true })), ("Function", function));
        var pattern = Dict(("PatternType", Num(2)), ("Shading", shading));
        maskedPage.Resources.Entries!["Pattern"] = Dict(("P", pattern));
        maskedPage.Streams[0].Value.Base64 = Convert.ToBase64String("/Pattern cs /P scn 0 0 100 100 re f"u8);
        var gradient = PdfCanvasExporter.Export(maskedDocument, 1).Commands.Single(c => c.Kind == "fillGradient");
        if (!gradient.Numbers.Skip(11).Take(3).SequenceEqual(new double[] { 1, 0, 0 }) || !gradient.Numbers.TakeLast(3).SequenceEqual(new double[] { 0, 0, 1 }))
        {
            throw new Exception("Stitching gradients must preserve their endpoint colors.");
        }
        var graphicsState = Dict(("BM", Name("Normal")), ("SMask", Name("None")));
        maskedPage.Resources.Entries!["ExtGState"] = Dict(("GS", graphicsState));
        maskedPage.Streams[0].Value.Base64 = Convert.ToBase64String("/GS gs"u8);
        foreach (var blend in new[] { Name("Normal"), Name("Compatible"), Array(Name("Normal"), Name("Compatible")) })
        {
            graphicsState.Entries!["BM"] = blend;
            if (PdfCanvasExporter.Export(maskedDocument, 1).Warnings.Count != 1)
            {
                throw new Exception("Default blending and disabled soft masks must not produce feature warnings.");
            }
        }
        graphicsState.Entries!["BM"] = Name("Multiply");
        graphicsState.Entries["SMask"] = Dict(("S", Name("Alpha")));
        var graphicsWarnings = PdfCanvasExporter.Export(maskedDocument, 1).Warnings;
        if (!graphicsWarnings.Any(w => w.Contains("Multiply")) || !graphicsWarnings.Contains("Graphics-state soft masks are not supported."))
        {
            throw new Exception("Unsupported blending and active soft masks must still be reported.");
        }
        var fontBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "RenderingTest.ttf"));
        var fontDocument = PdfReader.Read(PdfFixture.Create(fontBytes, new FixturePage("AB")));
        var embeddedDisplay = PdfCanvasExporter.Export(fontDocument, 1);
        var embeddedText = embeddedDisplay.Commands.Single(command => command.Kind == "text");
        if (embeddedDisplay.Fonts.Count != 1 || embeddedText.FontId != embeddedDisplay.Fonts[0].Id
            || embeddedText.Glyphs!.Any(glyph => glyph.RenderText is null || glyph.RenderText == glyph.Text))
        {
            throw new Exception("Embedded CID TrueType must use a browser font and independent rendering codes.");
        }
        var fontGraph = new PdfGraph(fontDocument);
        var fontPage = fontGraph.Pages()[0];
        var sourceFont = fontGraph.Get(fontPage.Resources, "Font")!.Entries!["F1"];
        var toUnicode = fontGraph.Get(sourceFont, "ToUnicode")!;
        var cmapText = Encoding.ASCII.GetString(PdfObjectKit.Parsers.PdfStreamDecoder.Decode(toUnicode));
        toUnicode.Entries!.Remove("Filter");
        toUnicode.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(cmapText.Replace("<0041>", "<0058>")));
        var changedUnicode = PdfCanvasExporter.Export(fontDocument, 1);
        if (changedUnicode.Fonts.Single().Id != embeddedDisplay.Fonts.Single().Id
            || changedUnicode.Commands.Single(command => command.Kind == "text").Glyphs![0].Text != "X")
        {
            throw new Exception("Extraction Unicode must not change CID rendering glyphs.");
        }
        var descendant = fontGraph.Resolve(fontGraph.Get(sourceFont, "DescendantFonts")!.Items![0]);
        var descriptor = fontGraph.Get(descendant, "FontDescriptor")!;
        var simple = Dict(("Type", Name("Font")), ("Subtype", Name("TrueType")), ("BaseFont", Name("PdfKitTest-Regular")),
            ("FontDescriptor", descriptor), ("Encoding", Name("WinAnsiEncoding")), ("FirstChar", Num(65)), ("Widths", Array(Num(600), Num(600))));
        fontGraph.Get(fontPage.Resources, "Font")!.Entries!["F1"] = simple;
        fontPage.Streams[0].Value.Entries!.Remove("Filter");
        fontPage.Streams[0].Value.Base64 = Convert.ToBase64String("BT /F1 24 Tf 10 100 Td (AB) Tj ET"u8);
        if (PdfCanvasExporter.Export(fontDocument, 1).Fonts.Count != 1)
        {
            throw new Exception("WinAnsi TrueType fonts must render through their embedded cmap.");
        }
        fontGraph.Get(descriptor, "FontFile2")!.Base64 = "AA==";
        var damagedFont = PdfCanvasExporter.Export(fontDocument, 1);
        if (damagedFont.Fonts.Count != 0 || !damagedFont.Warnings.Any(warning => warning.Contains("browser substitute")))
        {
            throw new Exception("Damaged fonts must fall back with a diagnostic.");
        }
        Console.WriteLine("Canvas exporter checks passed.");
    }
}
