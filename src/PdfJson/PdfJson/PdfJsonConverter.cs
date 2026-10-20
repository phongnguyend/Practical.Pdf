using System.Text.Json;
using System.Text.Json.Serialization;

namespace Practical.PdfJson;

public static class PdfJsonConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static DocumentModel ReadPdf(byte[] pdf, bool includeOriginal = false)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var graph = new PdfGraph(PdfNativeParser.Parse(pdf));
        var result = new DocumentModel();
        foreach (var page in graph.Pages())
        {
            var parsed = new PdfPageInterpreter(graph, page);
            if (parsed.TextWarning is not null) throw new InvalidDataException(parsed.TextWarning);
            var texts = new List<TextModel>();
            var word = ""; NativeLetter? first = null; NativeLetter? previous = null;
            void Flush()
            {
                if (first is not null && word.Length > 0) texts.Add(new() { Value = word, X = first.StartBaseLine.X, Y = first.StartBaseLine.Y, FontSize = first.FontSize });
                first = null; word = "";
            }
            var starts = new HashSet<int>();
            var offset = 0;
            foreach (var slot in parsed.Slots) { starts.Add(offset); offset += slot.Glyphs.Count; }
            var letterIndex = 0;
            foreach (var letter in parsed.Letters)
            {
                if (starts.Contains(letterIndex++)) Flush();
                if (previous is not null && Math.Abs(letter.StartBaseLine.Y - previous.StartBaseLine.Y) > 0.1) Flush();
                if (string.IsNullOrWhiteSpace(letter.Value)) Flush();
                else { first ??= letter; word += letter.Value; }
                previous = letter;
            }
            Flush();
            result.Pages.Add(new() { Width = page.Width, Height = page.Height, Text = texts });
        }
        if (includeOriginal)
            result.OriginalPdfBase64 = Convert.ToBase64String(pdf);
        return result;
    }

    public static string ToJson(DocumentModel document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static DocumentModel FromJson(string json)
    {
        var document = JsonSerializer.Deserialize<DocumentModel>(json, JsonOptions)
            ?? throw new InvalidDataException("JSON must contain a document object.");
        Validate(document);
        return document;
    }

    public static byte[] WritePdf(DocumentModel document, byte[]? trueTypeFont = null)
    {
        Validate(document);
        var graph = new PdfObjectDocument { Trailer = PdfPrimitives.Dict(("Root", PdfPrimitives.Ref(new(1)))) };
        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(graph.Objects.Count + 1); graph.Objects.Add(new(id, value)); return PdfPrimitives.Ref(id);
        }
        Add(PdfPrimitives.Dict(("Type", PdfPrimitives.Name("Catalog")), ("Pages", PdfPrimitives.Ref(new(2)))));
        var pages = PdfPrimitives.Dict(("Type", PdfPrimitives.Name("Pages")), ("Count", PdfPrimitives.Num(document.Pages.Count)), ("Kids", PdfPrimitives.Array()));
        Add(pages);
        PdfTrueTypeFont.Embedded? embedded = trueTypeFont is null ? null : new PdfTrueTypeFont(trueTypeFont).Embed(graph, string.Concat(document.Pages.SelectMany(p => p.Text).Select(t => t.Value)));
        var font = embedded?.Font ?? Add(PdfPrimitives.Dict(("Type", PdfPrimitives.Name("Font")), ("Subtype", PdfPrimitives.Name("Type1")), ("BaseFont", PdfPrimitives.Name("Helvetica")), ("Encoding", PdfPrimitives.Name("WinAnsiEncoding"))));
        foreach (var source in document.Pages)
        {
            var content = new System.Text.StringBuilder();
            foreach (var text in source.Text)
            {
                if (text.Value.Length == 0) continue;
                if (embedded is null && text.Value.Any(c => c < 32 || c > 126)) throw new InvalidDataException("Non-ASCII text requires a TrueType font (--font).");
                var bytes = embedded is null ? System.Text.Encoding.ASCII.GetBytes(text.Value) : embedded.Encode(text.Value).Bytes;
                string N(double n) => PdfPrimitives.Syntax(PdfPrimitives.Num(n));
                content.Append($"BT /F1 {N(text.FontSize)} Tf 1 0 0 1 {N(text.X)} {N(text.Y)} Tm {PdfPrimitives.Syntax(PdfPrimitives.Str(bytes))} Tj ET\n");
            }
            var stream = Add(PdfPrimitives.Stream(System.Text.Encoding.ASCII.GetBytes(content.ToString())));
            var page = Add(PdfPrimitives.Dict(("Type", PdfPrimitives.Name("Page")), ("Parent", PdfPrimitives.Ref(new(2))),
                ("MediaBox", PdfPrimitives.Array(PdfPrimitives.Num(0), PdfPrimitives.Num(0), PdfPrimitives.Num(source.Width), PdfPrimitives.Num(source.Height))),
                ("Resources", PdfPrimitives.Dict(("Font", PdfPrimitives.Dict(("F1", font))))), ("Contents", stream)));
            pages.Entries!["Kids"].Items!.Add(page);
        }
        return PdfObjectConverter.WriteObjects(graph);
    }
    // Explicitly separate restoration from rendering so JSON edits are never silently ignored.
    public static byte[] RestoreOriginal(DocumentModel document)
    {
        Validate(document);
        if (string.IsNullOrWhiteSpace(document.OriginalPdfBase64))
            throw new InvalidDataException("No original PDF is embedded. Export with --include-original first.");
        var bytes = Convert.FromBase64String(document.OriginalPdfBase64);
        _ = PdfNativeParser.Parse(bytes);
        return bytes;
    }

    private static void Validate(DocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1)
            throw new InvalidDataException("Only schemaVersion 1 is supported.");
        if (document.Pages is null || document.Pages.Count == 0)
            throw new InvalidDataException("At least one page is required.");
        foreach (var page in document.Pages)
        {
            if (page is null || !Positive(page.Width) || !Positive(page.Height) || page.Text is null)
                throw new InvalidDataException("Each page needs positive finite dimensions and a text array.");
            foreach (var text in page.Text)
            {
                if (text is null || text.Value is null || !Positive(text.FontSize)
                    || !double.IsFinite(text.X) || !double.IsFinite(text.Y))
                    throw new InvalidDataException("Each text entry needs a value, finite coordinates and a positive finite fontSize.");
                if (text.Value.Contains('\n') || text.Value.Contains('\r'))
                    throw new InvalidDataException("Use separate positioned text entries for each line.");
            }
        }
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}
