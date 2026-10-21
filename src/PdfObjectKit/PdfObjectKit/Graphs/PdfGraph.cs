using PdfObjectKit.Primitives;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using System.Globalization;
using System.Text;

namespace PdfObjectKit.Graphs;

internal sealed class PdfGraph(PdfObjectDocument document)
{
    private readonly Dictionary<PdfReference, PdfValue> objects = document.Objects.ToDictionary(o => o.Id, o => o.Value);
    public PdfValue Resolve(PdfValue value)
    {
        var seen = new HashSet<PdfReference>();
        while (value.Reference is { } id)
        {
            if (!seen.Add(id) || !objects.TryGetValue(id, out value!))
            {
                throw new InvalidDataException("Invalid/cyclic indirect reference.");
            }
        }

        return value;
    }

    public PdfValue? Get(PdfValue value, string key) => Resolve(value).Entries?.GetValueOrDefault(key)is { } item ? Resolve(item) : null;
    public double Number(PdfValue value, string key, double fallback = 0) => Get(value, key)is { } item ? PdfPrimitives.Number(item) : fallback;
    public List<NativePage> Pages()
    {
        var result = new List<NativePage>();
        var visited = new HashSet<PdfValue>();
        void Walk(PdfValue node, PdfValue? resources, PdfValue? media, int depth)
        {
            node = Resolve(node);
            if (depth > 100 || !visited.Add(node))
            {
                throw new InvalidDataException("Invalid page tree.");
            }

            resources = Get(node, "Resources") ?? resources;
            media = Get(node, "MediaBox") ?? media;
            if (Get(node, "Type")?.Name == "Page")
            {
                var box = media?.Items?.Select(PdfPrimitives.Number).ToArray() ?? [0, 0, 612, 792];
                if (box.Length != 4)
                {
                    throw new InvalidDataException("Invalid MediaBox.");
                }

                var streams = new List<(PdfReference Id, PdfValue Value)>();
                void Contents(PdfValue item, int nesting = 0)
                {
                    if (nesting > 100)
                    {
                        throw new InvalidDataException("Cyclic content array.");
                    }

                    var value = Resolve(item);
                    if (value.Type == "array")
                    {
                        foreach (var child in value.Items!)
                        {
                            Contents(child, nesting + 1);
                        }
                    }
                    else if (value.Type == "stream" && item.Reference is { } id)
                    {
                        streams.Add((id, value));
                    }
                    else
                    {
                        throw new InvalidDataException("Expected indirect content stream.");
                    }
                }

                if (node.Entries!.TryGetValue("Contents", out var content))
                {
                    Contents(content);
                }

                result.Add(new(result.Count + 1, node, resources ?? PdfPrimitives.Dict(), box[2] - box[0], box[3] - box[1], streams));
            }
            else
            {
                foreach (var child in Get(node, "Kids")?.Items ?? throw new InvalidDataException("Page tree Kids missing."))
                {
                    Walk(child, resources, media, depth + 1);
                }
            }
        }

        Walk(Get(Resolve(document.Trailer.Entries!["Root"]), "Pages")!, null, null, 0);
        return result;
    }
}
