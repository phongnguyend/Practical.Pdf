namespace Practical.PdfJson;
internal static class PdfFallbackFont
{
    internal sealed record Result(byte[] Bytes, double Width, string ResourceName);
    public static Result Create(PdfObjectDocument document, int pageNumber, string text, byte[] fontBytes)
    {
        var graph = new PdfGraph(document);
        var page = graph.Pages()[pageNumber - 1];
        var embedded = new PdfTrueTypeFont(fontBytes).Embed(document, text);
        var resources = new Dictionary<string, PdfValue>(page.Resources.Entries!);
        var fonts = graph.Get(page.Resources, "Font")?.Entries is { } existing ? new Dictionary<string, PdfValue>(existing) : [];
        var name = "PdfJsonEditFont";
        for (var i = 1; fonts.ContainsKey(name); i++) name = "PdfJsonEditFont" + i;
        fonts[name] = embedded.Font;
        resources["Font"] = new() { Type = "dictionary", Entries = fonts };
        page.Dictionary.Entries!["Resources"] = new() { Type = "dictionary", Entries = resources };
        var encoded = embedded.Encode(text);
        return new(encoded.Bytes, encoded.Width, name);
    }
}
