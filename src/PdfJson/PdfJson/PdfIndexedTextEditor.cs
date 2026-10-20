using System.Globalization;
using System.Text;

namespace Practical.PdfJson;

internal static class PdfIndexedTextEditor
{
    public static byte[] Apply(PdfObjectDocument document, byte[] original, PdfTextEditOptions options)
    {
        if (document.TextIndex is null)
        {
            throw new InvalidDataException("textIndex cannot be null.");
        }

        if (document.TextIndex.Count == 0)
        {
            return original;
        }

        var source = PdfNativeParser.Parse(original);
        var baseline = PdfTextIndexer.Build(source);
        var seen = new HashSet<int>();
        var edits = new List<(PdfPageTextIndex Requested, PdfPageTextIndex Actual)>();
        foreach (var entry in document.TextIndex)
        {
            if (entry is null || entry.Text is null || entry.PageNumber < 1 || entry.PageNumber > baseline.Count || !seen.Add(entry.PageNumber))
            {
                throw new InvalidDataException("textIndex requires unique, valid page numbers and non-null text.");
            }

            var actual = baseline[entry.PageNumber - 1];
            var keys = new HashSet<(int, int)>();
            foreach (var run in entry.Runs ?? [])
            {
                if (run is null || run.Text is null || !keys.Add((run.OperatorIndex, run.OperandIndex)))
                {
                    throw new InvalidDataException("Null or duplicate text run.");
                }

                var match = actual.Runs?.SingleOrDefault(r => r.OperatorIndex == run.OperatorIndex && r.OperandIndex == run.OperandIndex);
                if (match is null || match.StreamObject != run.StreamObject || match.StreamSha256 != run.StreamSha256 || match.OriginalText != run.OriginalText)
                {
                    throw new InvalidDataException($"Stale or invalid text run on page {entry.PageNumber}. Re-export before editing.");
                }
            }

            var changed = entry.Runs?.Any(r => r.Text != r.OriginalText) == true;
            if (changed && actual.Text != entry.Text)
            {
                throw new InvalidDataException("Edit runs[].text or page text, not both.");
            }

            if (actual.Text != entry.Text || changed)
            {
                edits.Add((entry, actual));
            }
        }

        if (edits.Count == 0)
        {
            return original;
        }

        var working = PdfObjectConverter.FromJson(PdfObjectConverter.ToJson(document));
        var graph = new PdfGraph(source);
        var pages = graph.Pages();
        var expected = baseline.ToDictionary(p => p.PageNumber, p => p.Text);
        foreach (var(requested, actual)in edits)
        {
            void Reject(string message) => throw new InvalidDataException($"Cannot edit textIndex page {requested.PageNumber}: {message}");
            if (actual.Warning is not null)
            {
                Reject(actual.Warning);
            }

            if (actual.ContentStreams.Count != 1 || actual.FormStreams.Count != 0)
            {
                Reject("editing requires one content stream and no Form XObject resources.");
            }

            var id = actual.ContentStreams[0];
            if (CountReferences(document, id) != 1)
            {
                Reject("the content stream is shared.");
            }

            var page = new PdfPageInterpreter(graph, pages[requested.PageNumber - 1]);
            if (page.Operations.Any(o => o.Operator is "'" or "\"" or "BI"))
            {
                Reject("quote operators and inline images are unsupported for editing.");
            }

            if (page.Operations.Any(o => (o.Operator is "Tc" or "Tw") && PdfPrimitives.Number(o.Operands[0]) != 0))
            {
                Reject("nonzero character/word spacing is unsupported for editing.");
            }

            var replacements = new Dictionary<NativeSlot, string>();
            if (requested.Runs?.Any(r => r.Text != r.OriginalText) == true)
            {
                foreach (var run in requested.Runs.Where(r => r.Text != r.OriginalText))
                {
                    replacements[page.Slots.Single(s => s.Operation == run.OperatorIndex && s.Token == run.OperandIndex)] = run.Text;
                }
            }
            else
            {
                var start = 0;
                while (start < actual.Text.Length && start < requested.Text.Length && actual.Text[start] == requested.Text[start])
                {
                    start++;
                }

                var suffix = 0;
                while (suffix < actual.Text.Length - start && suffix < requested.Text.Length - start && actual.Text[^(suffix + 1)] == requested.Text[^(suffix + 1)])
                {
                    suffix++;
                }

                var end = actual.Text.Length - suffix;
                var slot = page.Slots.FirstOrDefault(s => start >= s.Start && end <= s.Start + s.Text.Length && start <= s.Start + s.Text.Length);
                if (slot is null)
                {
                    Reject("edit crosses text operands. Edit runs[].text instead.");
                }

                replacements[slot!] = slot!.Text[..(start - slot.Start)] + requested.Text.Substring(start, requested.Text.Length - start - suffix) + slot.Text[(end - slot.Start)..];
            }

            expected[requested.PageNumber] = string.Concat(page.Slots.Select(s => replacements.GetValueOrDefault(s, s.Text)));
            var changedOperations = new Dictionary<(int, int), string>();
            foreach (var(slot, text)in replacements)
            {
                if (text.Contains('\n') || text.Contains('\r'))
                {
                    Reject("newlines require layout changes.");
                }

                var alphabet = slot.Font.Alphabet().OrderByDescending(g => g.Text.Length).ToList();
                var encoded = new List<byte>();
                var width = 0d;
                var missing = false;
                for (var p = 0; p < text.Length;)
                {
                    var glyph = alphabet.FirstOrDefault(g => text.AsSpan(p).StartsWith(g.Text, StringComparison.Ordinal));
                    if (glyph is null)
                    {
                        missing = true;
                        break;
                    }

                    encoded.AddRange(glyph.Bytes);
                    width += glyph.Width;
                    p += glyph.Text.Length;
                }

                string? font = null;
                if (missing)
                {
                    if (options.FallbackFont is null)
                    {
                        Reject("replacement glyph unavailable; supply --font path/to/font.ttf.");
                    }

                    var fallback = PdfFallbackFont.Create(working, requested.PageNumber, text, options.FallbackFont!);
                    encoded = fallback.Bytes.ToList();
                    width = fallback.Width;
                    font = fallback.ResourceName;
                }

                var adjustment = PdfPrimitives.Syntax(PdfPrimitives.Num((width - slot.Glyphs.Sum(g => g.Width)) * 1000));
                var data = PdfPrimitives.Syntax(PdfPrimitives.Str(encoded.ToArray()));
                changedOperations[(slot.Operation, slot.Token)] = font is null ? $"[{data} {adjustment}] TJ\n" : $"q\n/{font} {slot.FontSize.ToString("G17", CultureInfo.InvariantCulture)} Tf\n{data} Tj\nQ\n[{adjustment}] TJ\n";
            }

            var content = new StringBuilder();
            for (var i = 0; i < page.Operations.Count; i++)
            {
                var op = page.Operations[i];
                if (changedOperations.TryGetValue((i, -1), out var replacement))
                {
                    content.Append(replacement);
                }
                else if (op.Operator == "TJ" && changedOperations.Keys.Any(k => k.Item1 == i))
                {
                    for (var j = 0; j < op.Operands[0].Items!.Count; j++)
                    {
                        content.Append(changedOperations.GetValueOrDefault((i, j), "[" + PdfPrimitives.Syntax(op.Operands[0].Items![j]) + "] TJ\n"));
                    }
                }
                else
                {
                    content.Append(op.Serialize());
                }
            }

            var stream = working.Objects.Single(o => o.Id == id).Value;
            stream.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(content.ToString()));
            stream.Entries!.Remove("Filter");
            stream.Entries.Remove("DecodeParms");
            stream.Entries.Remove("Length");
        }

        var output = PdfObjectConverter.WriteObjects(working);
        var verified = PdfTextIndexer.Build(PdfNativeParser.Parse(output));
        foreach (var entry in verified)
        {
            if (entry.Text != expected[entry.PageNumber] || (edits.Any(e => e.Requested.PageNumber == entry.PageNumber) && entry.Warning is not null))
            {
                throw new InvalidDataException($"Text edit verification failed on page {entry.PageNumber}.");
            }
        }

        return output;
    }

    private static int CountReferences(PdfObjectDocument document, PdfReference id)
    {
        int Count(PdfValue value) => (value.Reference == id ? 1 : 0) + (value.Items?.Sum(Count) ?? 0) + (value.Entries?.Values.Sum(Count) ?? 0);
        return document.Objects.Sum(o => Count(o.Value)) + Count(document.Trailer);
    }
}
