using System.Globalization;
using PdfObjectKit.Graphs;

namespace PdfObjectKit.Fonts;

internal static class PdfStandardGlyphBounds
{
    private sealed record Metrics(Dictionary<int, PdfGlyphBounds> Codes, Dictionary<string, PdfGlyphBounds> Unicode);

    private static readonly Dictionary<string, Metrics> Fonts = Load();

    public static bool TryGet(PdfGraph graph, PdfNativeFont font, NativeGlyph glyph, out PdfGlyphBounds bounds)
    {
        bounds = default;
        var source = font.Source;
        if (graph.Get(source, "Subtype")?.Name != "Type1" || graph.Get(source, "FontDescriptor") is not null
            || !Fonts.TryGetValue(graph.Get(source, "BaseFont")?.Name ?? "", out var metrics) || glyph.Bytes.Length != 1)
        {
            return false;
        }
        var encoding = graph.Get(source, "Encoding");
        if (encoding is null || encoding.Name == "StandardEncoding")
        {
            return metrics.Codes.TryGetValue(glyph.Bytes[0], out bounds);
        }
        return encoding.Name is "WinAnsiEncoding" or "MacRomanEncoding"
            && font.EncodingUnicode.TryGetValue(Convert.ToHexString(glyph.Bytes), out var text)
            && metrics.Unicode.TryGetValue(text, out bounds);
    }

    private static Dictionary<string, Metrics> Load()
    {
        var result = new Dictionary<string, Metrics>();
        var assembly = typeof(PdfStandardGlyphBounds).Assembly;
        const string prefix = "PdfObjectKit.Fonts.StandardMetrics.";
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".afm", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            var metrics = new Metrics([], []);
            while (reader.ReadLine() is { } line)
            {
                if (!line.StartsWith("C ", StringComparison.Ordinal))
                {
                    continue;
                }
                var parts = line.Split(';').Select(part => part.Trim()).ToArray();
                var code = int.Parse(parts[0][2..], CultureInfo.InvariantCulture);
                var name = parts.Single(part => part.StartsWith("N ", StringComparison.Ordinal))[2..];
                var box = parts.Single(part => part.StartsWith("B ", StringComparison.Ordinal))[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => double.Parse(value, CultureInfo.InvariantCulture) / 1000).ToArray();
                var bounds = new PdfGlyphBounds(box[0], box[1], box[2], box[3]);
                if (code >= 0)
                {
                    metrics.Codes[code] = bounds;
                }
                if (PdfNativeFont.GlyphName(name) is { } unicode)
                {
                    metrics.Unicode[unicode] = bounds;
                }
            }
            result.Add(resource[prefix.Length..^4], metrics);
        }
        return result;
    }
}
