using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
using System.Security.Cryptography;

namespace PdfObjectKit.Indexers;

internal static class PdfTextIndexer
{
    public static List<PdfPageTextIndex> Build(PdfObjectDocument document)
    {
        var graph = new PdfGraph(document);
        return graph.Pages().Select(page =>
        {
            var parsed = new PdfPageInterpreter(graph, page);
            var entry = new PdfPageTextIndex
            {
                PageNumber = page.Number,
                Text = parsed.Text,
                Warning = parsed.TextWarning,
                ContentStreams = page.Streams.Select(s => s.Id).ToList()
            };
            var visited = new HashSet<PdfReference>();
            void Forms(PdfValue resources, int depth)
            {
                if (depth > 100)
                {
                    throw new InvalidDataException("Form resource nesting exceeds limit.");
                }

                foreach (var item in graph.Get(resources, "XObject")?.Entries?.Values ?? Enumerable.Empty<PdfValue>())
                {
                    if (item.Reference is not { } id || !visited.Add(id))
                    {
                        continue;
                    }

                    var form = graph.Resolve(item);
                    if (graph.Get(form, "Subtype")?.Name != "Form")
                    {
                        continue;
                    }

                    entry.FormStreams.Add(id);
                    if (graph.Get(form, "Resources")is { } nested)
                    {
                        Forms(nested, depth + 1);
                    }
                }
            }

            try
            {
                Forms(page.Resources, 0);
            }
            catch (Exception ex)when (ex is not OutOfMemoryException)
            {
                entry.Warning ??= ex.Message;
            }

            if (entry.Warning is not null || page.Streams.Count > 1 || entry.FormStreams.Count != 0 || parsed.Operations.Any(o => o.Operator is "'" or "\"" or "BI"))
            {
                entry.RunWarning = "Editable runs require supported fonts, at most one content stream, and no forms, quote operators or inline images.";
            }
            else
            {
                entry.Runs = parsed.Slots.Select(slot => new PdfTextRun { StreamObject = slot.Stream, OperatorIndex = slot.Operation, OperandIndex = slot.Token, StreamSha256 = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(page.Streams.Single().Value.Base64!))), Text = slot.Text, FontName = slot.Font.Name, FontSize = slot.FontSize }).ToList();
            }

            return entry;
        }).ToList();
    }
}
