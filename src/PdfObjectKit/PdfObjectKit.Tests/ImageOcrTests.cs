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

internal static class ImageOcrTests
{
    public static void Run()
    {
        var font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "RenderingTest.ttf"));
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        foreach (var replace in new[] { false, true })
        {
            var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("AB")));
            var originalPage = new PdfGraph(document).Pages()[0];
            var originalText = string.Join("\n", originalPage.Streams.Select(stream => Encoding.Latin1.GetString(PdfStreamDecoder.Decode(stream.Value))));
            PdfImageEditor.InsertImage(document, 1, png, 10, 10, 100, 50);
            PdfValue Add(PdfValue value)
            {
                var id = new PdfReference(document.Objects.Max(item => item.Id.ObjectNumber) + 1);
                document.Objects.Add(new(id, value));
                return Ref(id);
            }
            var graph = new PdfGraph(document);
            var page = graph.Pages()[0];
            var imageName = graph.Get(page.Resources, "XObject")!.Entries!.Keys.Single();
            var form = Stream(Encoding.ASCII.GetBytes($"/{imageName} Do"));
            form.Entries!["Subtype"] = Name("Form");
            form.Entries["BBox"] = Array(Num(0), Num(0), Num(1), Num(1));
            form.Entries["Resources"] = page.Resources;
            form.Entries["Matrix"] = Array(Num(1), Num(.1), Num(.2), Num(1), Num(0), Num(0));
            page.Dictionary.Entries!["Resources"] = Dict(("XObject", Dict(("Fm", Add(form)))), ("Font", graph.Get(page.Resources, "Font")!));
            page.Dictionary.Entries["Contents"] = Add(Stream(Encoding.ASCII.GetBytes(originalText + "\nq 0 100 -200 20 300 40 cm /Fm Do Q q 50 0 0 50 20 20 cm /Fm Do Q")));
            page.Dictionary.Entries["Rotate"] = Num(90);
            var corners = PdfImageExtractor.Extract(document)[0].Images[0].Corners;
            PdfImageOcrEditor.Apply(document, 1, 1, [new("AB", .1, .2, .5, .1, .28)], font, replace);
            var roundTrip = PdfReader.Read(PdfWriter.Write(document));
            var images = PdfImageExtractor.Extract(roundTrip)[0].Images;
            var selections = PdfParagraphExtractor.ExtractSelectionLines(roundTrip, 1);
            var originalSelection = selections.First(selection => Math.Abs(selection.Baseline - 100) < .001);
            if (originalSelection.Left != 20 || originalSelection.Right <= originalSelection.Left || originalSelection.Height != 12)
            {
                throw new Exception("Selection metadata must preserve the existing horizontal line geometry contract.");
            }

            if (selections.Count(selection => selection.Text == "AB") != 2
                || !selections.Any(selection => Math.Abs(selection.Corners[1].Y - selection.Corners[0].Y) > 1))
            {
                throw new Exception("OCR must leave original horizontal text and transformed OCR text selectable.");
            }

            if (images.Count != (replace ? 1 : 2))
            {
                throw new Exception("OCR changes only the selected image occurrence.");
            }
            var updated = new PdfGraph(roundTrip).Pages()[0];
            var ops = updated.Streams.SelectMany(stream => PdfContent.Parse(PdfStreamDecoder.Decode(stream.Value))).ToList();
            var matrix = ops.Last(op => op.Operator == "Tm").Operands.Select(value => double.Parse(value.Number!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var x = corners[0].X + (corners[1].X - corners[0].X) * .1 + (corners[3].X - corners[0].X) * .72;
            var y = corners[0].Y + (corners[1].Y - corners[0].Y) * .1 + (corners[3].Y - corners[0].Y) * .72;
            if (matrix.Take(4).Any(value => Math.Abs(value) > 500) || Math.Abs(matrix[4] - x) > .00001 || Math.Abs(matrix[5] - y) > .00001 || ops.Last(op => op.Operator == "Tr").Operands[0].Number != (replace ? "0" : "3"))
            {
                throw new Exception("OCR must preserve nested, skewed and rotated placement and text visibility.");
            }
            // OCR normalizes top, height and baseline independently. This baseline rounds
            // slightly above top + height even though it is exactly the pixel bottom.
            var edgeLine = new PdfOcrTextLine("AB", .1, 6d / 240, .5, 17d / 240, 23d / 240);
            if (edgeLine.Baseline <= edgeLine.Y + edgeLine.Height)
            {
                throw new Exception("The fixture must reproduce independent normalization rounding.");
            }
            PdfImageOcrEditor.Apply(document, 1, 1, [edgeLine], font);
            var beforeInvalid = PdfConverter.ToJson(document);
            foreach (var invalid in new[] { edgeLine with { Baseline = edgeLine.Baseline + .001 }, edgeLine with { Baseline = edgeLine.Y - .001 }, edgeLine with { Baseline = double.NaN }, edgeLine with { Width = 2 } })
            {
                try
                {
                    PdfImageOcrEditor.Apply(document, 1, 1, [invalid], font, true);
                    throw new Exception("Genuinely invalid OCR coordinates must still be rejected.");
                }
                catch (ArgumentException)
                {
                    if (PdfConverter.ToJson(document) != beforeInvalid)
                    {
                        throw new Exception("Invalid OCR coordinates must not modify the document.");
                    }
                }
            }
            var before = PdfConverter.ToJson(document);
            try
            {
                PdfImageOcrEditor.Apply(document, 1, 1, [new("AB", .1, .2, .5, .1, .28)], [0, 1], true);
                throw new Exception("Invalid font should reject OCR.");
            }
            catch (InvalidDataException)
            {
                if (PdfConverter.ToJson(document) != before)
                {
                    throw new Exception("Failed OCR must not remove the image or modify resources.");
                }
            }
        }
        Console.WriteLine("Passed OCR placement, occurrence isolation, visibility and atomicity checks.");
    }
}
