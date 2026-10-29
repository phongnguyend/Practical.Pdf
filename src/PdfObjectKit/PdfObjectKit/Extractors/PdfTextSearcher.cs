using System.Globalization;
using System.Text;
using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
using PdfObjectKit.Options;
using PdfObjectKit.Validators;

namespace PdfObjectKit.Extractors;

/// <summary>Literal Unicode text search with glyph highlights. No OCR is performed.</summary>
public static class PdfTextSearcher
{
    public static PdfTextSearchResult Search(PdfObjectDocument document, string query, PdfTextSearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        PdfTextSearchIndex.ValidateQuery(query, options ?? new());
        return CreateIndex(document, cancellationToken).Search(query, options, cancellationToken);
    }

    /// <summary>Build once for repeated searches. Rebuild after modifying the document.</summary>
    public static PdfTextSearchIndex CreateIndex(PdfObjectDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfDocumentValidator.Validate(document);
        var graph = new PdfGraph(document);
        var pages = new List<PdfTextSearchIndex.Page>();
        var warnings = new List<string>();
        var total = 0;
        foreach (var page in graph.Pages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = new PdfPageInterpreter(graph, page);
            if (parsed.TextWarning is not null)
            {
                warnings.Add($"Page {page.Number}: search may be incomplete: {parsed.TextWarning}");
            }
            var slots = parsed.Slots.Where(slot => !parsed.TextStreams.TryGetValue(slot.Stream, out var stream)
                || stream.Entries?.ContainsKey("PdfObjectKitWatermark") != true).ToList();
            var rows = new List<List<NativeSlot>>();
            foreach (var slot in slots.Where(slot => slot.IsHorizontal).OrderByDescending(slot => slot.BaselineStart.Y).ThenBy(slot => slot.BaselineStart.X))
            {
                var row = rows.LastOrDefault();
                if (row is null || Math.Abs(row[0].BaselineStart.Y - slot.BaselineStart.Y) > Math.Min(row[0].Height, slot.Height) * .25)
                {
                    row = [];
                    rows.Add(row);
                }
                row.Add(slot);
            }
            // Non-horizontal text retains drawing order, separated from horizontal reading order.
            rows.AddRange(slots.Where(slot => !slot.IsHorizontal).Select(slot => new List<NativeSlot> { slot }));
            var text = new StringBuilder();
            var mapping = new List<int>();
            var regions = new List<PdfSearchRegion>();
            void Separator(char separator)
            {
                text.Append(separator);
                mapping.Add(-1);
            }
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (text.Length > 0)
                {
                    Separator('\n');
                }
                NativeSlot? previous = null;
                foreach (var slot in row.OrderBy(slot => slot.IsHorizontal ? slot.BaselineStart.X : 0))
                {
                    if (previous is not null)
                    {
                        var gap = slot.BaselineStart.X - previous.BaselineEnd.X;
                        // Do not manufacture phrase matches between distant columns on the same row.
                        if (gap > Math.Max(previous.Height, slot.Height) * 2)
                        {
                            Separator('\0');
                        }
                        else if (gap > Math.Min(previous.Height, slot.Height) * .15 && text.Length > 0 && !char.IsWhiteSpace(text[^1]))
                        {
                            Separator(' ');
                        }
                    }
                    var x = 0d;
                    foreach (var glyph in slot.Glyphs)
                    {
                        var width = glyph.Width * slot.FontSize * slot.HorizontalScale;
                        var matrix = slot.TextMatrix;
                        var bottom = slot.Rise - slot.FontSize * .2;
                        var top = slot.Rise + slot.FontSize * .8;
                        var region = new PdfSearchRegion([matrix.Point(x, bottom), matrix.Point(x + width, bottom), matrix.Point(x + width, top), matrix.Point(x, top)]);
                        var id = regions.Count;
                        regions.Add(region);
                        text.Append(glyph.Text);
                        mapping.AddRange(Enumerable.Repeat(id, glyph.Text.Length));
                        x += width + (slot.CharacterSpacing + (glyph.Bytes.Length == 1 && glyph.Bytes[0] == 32 ? slot.WordSpacing : 0)) * slot.HorizontalScale;
                        if (text.Length > 2_000_000)
                        {
                            throw new NotSupportedException("Search supports up to two million text characters per page.");
                        }
                    }
                    previous = slot;
                }
            }
            total += text.Length;
            if (total > 5_000_000)
            {
                throw new NotSupportedException("Search supports up to five million text characters per document.");
            }
            var normalized = PdfTextSearchIndex.Normalize(text.ToString(), mapping);
            pages.Add(new(page.Number, normalized.Text, normalized.Mapping, regions));
            if (string.IsNullOrWhiteSpace(normalized.Text))
            {
                warnings.Add($"Page {page.Number}: no searchable text. Image-only pages require OCR.");
            }
        }
        return new(pages, warnings);
    }
}

/// <summary>An independent, reusable text snapshot. Match offsets refer to normalized page text.</summary>
public sealed class PdfTextSearchIndex
{
    internal sealed record Page(int Number, string Text, List<int[]> Mapping, List<PdfSearchRegion> Regions);

    private readonly List<Page> pages;
    private readonly IReadOnlyList<string> warnings;

    internal PdfTextSearchIndex(List<Page> pages, IReadOnlyList<string> warnings)
    {
        this.pages = pages;
        this.warnings = warnings;
    }

    public PdfTextSearchResult Search(string query, PdfTextSearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        ValidateQuery(query, options);
        cancellationToken.ThrowIfCancellationRequested();
        var needle = Normalize(query, null).Text.Trim();
        var matches = new List<PdfTextSearchMatch>();
        if (needle.Length == 0)
        {
            return new(matches, false, warnings);
        }
        foreach (var page in pages)
        {
            for (var offset = 0; offset <= page.Text.Length - needle.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = page.Text.IndexOf(needle, offset, options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    break;
                }
                var end = start + needle.Length;
                offset = start + 1;
                if (options.WholeWord && (IsWord(page.Text, start - 1) || IsWord(page.Text, end)))
                {
                    continue;
                }
                if (matches.Count == options.MaxResults)
                {
                    return new(matches, true, warnings);
                }
                var regions = page.Mapping.Skip(start).Take(needle.Length).SelectMany(ids => ids).Where(id => id >= 0).Distinct().Select(id => page.Regions[id]).ToArray();
                var snippetStart = Math.Max(0, start - 40);
                var snippetEnd = Math.Min(page.Text.Length, end + 40);
                matches.Add(new(page.Number, start, page.Text[start..end], page.Text[snippetStart..snippetEnd].Replace('\0', ' '), regions));
            }
        }
        return new(matches, false, warnings);
    }

    internal static void ValidateQuery(string query, PdfTextSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length > 256 || options.MaxResults is < 1 or > 2000 || query.Contains('\0'))
        {
            throw new ArgumentException("Search requires a query up to 256 characters without NUL, and 1–2000 maximum results.");
        }
    }

    private static bool IsWord(string text, int index)
    {
        if (index < 0 || index >= text.Length)
        {
            return false;
        }
        if (char.IsLowSurrogate(text[index]) && index > 0 && char.IsHighSurrogate(text[index - 1]))
        {
            index--;
        }
        var category = CharUnicodeInfo.GetUnicodeCategory(text, index);
        return char.IsLetterOrDigit(text, index) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ConnectorPunctuation;
    }

    internal static (string Text, List<int[]> Mapping) Normalize(string source, List<int>? mapping)
    {
        var text = new StringBuilder();
        var result = new List<int[]>();
        var elements = StringInfo.GetTextElementEnumerator(source);
        while (elements.MoveNext())
        {
            var original = elements.GetTextElement();
            var ids = mapping is null ? [] : mapping.Skip(elements.ElementIndex).Take(original.Length).Distinct().ToArray();
            foreach (var character in original.Normalize(NormalizationForm.FormKC))
            {
                if (char.IsWhiteSpace(character))
                {
                    if (text.Length > 0 && text[^1] == ' ')
                    {
                        result[^1] = result[^1].Concat(ids).Distinct().ToArray();
                        continue;
                    }
                    text.Append(' ');
                }
                else
                {
                    text.Append(character);
                }
                result.Add(ids);
            }
        }
        return (text.ToString(), result);
    }
}
