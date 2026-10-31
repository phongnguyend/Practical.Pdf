using System.Text;
using PdfObjectKit.Extractors;
using PdfObjectKit.Fonts;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

public static class PdfImageOcrEditor
{
    /// <summary>Adds a searchable layer, or replaces one image occurrence with visible OCR text.</summary>
    public static void Apply(PdfObjectDocument document, int pageNumber, int imageNumber, IReadOnlyList<PdfOcrTextLine> lines, byte[] fontBytes, bool replaceImage = false)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(fontBytes);
        if (lines.Count is 0 or > 2000 || lines.Sum(line => (long)(line?.Text?.Length ?? 0)) > 100000 || fontBytes.Length is 0 or > 5000000)
        {
            throw new ArgumentException("Provide 1-2000 OCR lines, at most 100000 characters, and a TrueType font up to 5 MB.");
        }
        const double coordinateTolerance = 1e-9;
        var normalized = new List<PdfOcrTextLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line is null || string.IsNullOrWhiteSpace(line.Text) || line.Text.Any(char.IsControl))
            {
                throw new ArgumentException($"OCR line {i + 1} requires nonempty text without control characters.");
            }
            if (!new[] { line.X, line.Y, line.Width, line.Height, line.Baseline }.All(double.IsFinite)
                || line.X < 0 || line.Y < 0 || line.Width <= 0 || line.Height <= 0
                || line.X + line.Width > 1.000001 || line.Y + line.Height > 1.000001)
            {
                throw new ArgumentException($"OCR line {i + 1} requires finite normalized bounds inside the image.");
            }
            // Independently normalized pixel coordinates can differ by one rounding unit:
            // bottom / height is not always equal to top / height + (bottom - top) / height.
            var bottom = line.Y + line.Height;
            if (line.Baseline < line.Y - coordinateTolerance || line.Baseline > bottom + coordinateTolerance)
            {
                throw new ArgumentException($"OCR line {i + 1} has a baseline outside its bounds.");
            }
            normalized.Add(line with { Baseline = Math.Clamp(line.Baseline, line.Y, bottom) });
        }
        PdfPageEditor.Edit(document, working =>
        {
            var pages = PdfImageExtractor.Extract(working);
            if (pageNumber < 1 || pageNumber > pages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pageNumber));
            }
            var index = pages[pageNumber - 1];
            var image = index.Images.SingleOrDefault(item => item.ImageNumber == imageNumber);
            if (index.Warning is not null || image is null || image.IsImageMask)
            {
                throw new NotSupportedException("OCR requires a fully indexed image occurrence, excluding stencil masks.");
            }
            var origin = image.Corners[0];
            var a = image.Corners[1].X - origin.X;
            var b = image.Corners[1].Y - origin.Y;
            var c = image.Corners[3].X - origin.X;
            var d = image.Corners[3].Y - origin.Y;
            if (!double.IsFinite(a * d - b * c) || Math.Abs(a * d - b * c) < 1e-10)
            {
                throw new NotSupportedException("The image placement is degenerate.");
            }
            if (replaceImage)
            {
                PdfPageEditor.DeleteImage(working, pageNumber, imageNumber);
            }
            var graph = new PdfGraph(working);
            var page = graph.Pages()[pageNumber - 1];
            PdfImageEditor.ValidateContent(page);
            var embedded = new PdfTrueTypeFont(fontBytes).Embed(working, string.Concat(normalized.Select(line => line.Text)));
            var resources = Dict();
            resources.Entries = new(page.Resources.Entries!);
            var fonts = Dict();
            fonts.Entries = new(graph.Get(page.Resources, "Font")?.Entries ?? []);
            var name = "PdfObjectKitOcr";
            while (fonts.Entries.ContainsKey(name))
            {
                name += "_";
            }
            fonts.Entries[name] = embedded.Font;
            resources.Entries["Font"] = fonts;
            page.Dictionary.Entries!["Resources"] = resources;
            var original = string.Join("\n", page.Streams.Select(stream => Encoding.Latin1.GetString(PdfStreamDecoder.Decode(stream.Value))));
            var content = new StringBuilder($"q\n{original}\nQ\nn\nq BT /{name} 1 Tf 0 g {(replaceImage ? 0 : 3)} Tr\n");
            foreach (var line in normalized)
            {
                var encoded = embedded.Encode(line.Text);
                if (encoded.Width <= 0)
                {
                    throw new ArgumentException("OCR line has no measurable glyph width.");
                }
                var horizontal = line.Width / encoded.Width;
                var vertical = line.Height / 0.8;
                var x = origin.X + a * line.X + c * (1 - line.Baseline);
                var y = origin.Y + b * line.X + d * (1 - line.Baseline);
                var matrix = new[] { a * horizontal, b * horizontal, c * vertical, d * vertical, x, y };
                content.AppendLine(string.Join(" ", matrix.Select(value => Syntax(Num(value)))) + " Tm " + Syntax(Str(encoded.Bytes)) + " Tj");
            }
            content.AppendLine("ET Q");
            var id = new PdfReference(checked(working.Objects.Max(item => item.Id.ObjectNumber) + 1));
            working.Objects.Add(new(id, Stream(Encoding.Latin1.GetBytes(content.ToString()))));
            page.Dictionary.Entries["Contents"] = Ref(id);
        });
    }
}
