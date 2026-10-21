using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Primitives;
using PdfObjectKit.Writers;
using PdfObjectKit.Models;
using PdfObjectKit.Indexers;
using PdfObjectKit.Parsers;
using System.Globalization;
using System.Text;

namespace PdfObjectKit.Editors;

internal static class PdfTextRewriter
{
    public static byte[] Apply(PdfObjectDocument document, int pageNumber, PdfTextRun target,
        string replacementText, PdfTextEditOptions options)
    {
        var source = document;
        var baseline = PdfTextIndexer.Build(source);
        var working = PdfNativeParser.Parse(PdfWriter.Write(document));
        var graph = new PdfGraph(source);
        var pages = graph.Pages();
        var expected = baseline.ToDictionary(p => p.PageNumber, p => p.Text);
        var actual = baseline[pageNumber - 1];
        void Reject(string message) => throw new InvalidDataException($"Cannot edit page {pageNumber}: {message}");
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

        var page = new PdfPageInterpreter(graph, pages[pageNumber - 1]);
        if (page.Operations.Any(o => o.Operator is "'" or "\"" or "BI"))
        {
            Reject("quote operators and inline images are unsupported for editing.");
        }

        if (page.Operations.Any(o => (o.Operator is "Tc" or "Tw") && PdfPrimitives.Number(o.Operands[0]) != 0))
        {
            Reject("nonzero character/word spacing is unsupported for editing.");
        }

        var selected = page.Slots.Single(s => s.Stream == target.StreamObject
            && s.Operation == target.OperatorIndex && s.Token == target.OperandIndex);
        var replacements = new Dictionary<NativeSlot, string> { [selected] = replacementText };

        expected[pageNumber] = string.Concat(page.Slots.Select(s => replacements.GetValueOrDefault(s, s.Text)));
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
                    Reject("replacement glyph unavailable; supply PdfTextEditOptions.FallbackFont.");
                }

                var fallback = PdfFallbackFont.Create(working, pageNumber, text, options.FallbackFont!);
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

        var output = PdfWriter.WriteObjects(working);
        var verified = PdfTextIndexer.Build(PdfNativeParser.Parse(output));
        foreach (var entry in verified)
        {
            if (entry.Text != expected[entry.PageNumber] || (entry.PageNumber == pageNumber && entry.Warning is not null))
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
