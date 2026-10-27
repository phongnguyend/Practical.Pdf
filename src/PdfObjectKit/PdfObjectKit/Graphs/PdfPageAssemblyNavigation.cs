using PdfObjectKit.Models;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Graphs;

internal sealed class PdfPageAssemblyNavigation
{
    private readonly PdfGraph graph;
    private readonly PdfValue catalog;
    private readonly HashSet<PdfValue> selected;
    private readonly Dictionary<string, PdfValue> destinations = [];

    public PdfPageAssemblyNavigation(PdfObjectDocument document, HashSet<PdfValue> selected)
    {
        this.selected = selected;
        graph = new(document);
        catalog = graph.Resolve(document.Trailer.Entries!["Root"]);
        foreach (var pair in graph.Get(catalog, "Dests")?.Entries ?? [])
        {
            destinations[pair.Key] = pair.Value;
        }
        var visited = new HashSet<PdfValue>();
        void Read(PdfValue raw, int depth)
        {
            var node = graph.Resolve(raw);
            if (depth > 100 || !visited.Add(node))
            {
                throw new InvalidDataException("Invalid destination name tree.");
            }
            var entries = graph.Get(node, "Names")?.Items ?? [];
            if (entries.Count % 2 != 0)
            {
                throw new InvalidDataException("Destination name tree must contain key/value pairs.");
            }
            for (var i = 0; i + 1 < entries.Count; i += 2)
            {
                var key = graph.Resolve(entries[i]);
                if (key.Type != "string" || !destinations.TryAdd(PdfFormGraph.Text(key), entries[i + 1]))
                {
                    throw new InvalidDataException("Invalid or duplicate destination name.");
                }
            }
            foreach (var child in graph.Get(node, "Kids")?.Items ?? [])
            {
                Read(child, depth + 1);
            }
        }
        if (graph.Get(catalog, "Names") is { } names)
        {
            if (names.Entries!.Keys.Any(key => key is not ("Dests" or "EmbeddedFiles")))
            {
                throw new NotSupportedException("Name trees other than destinations and embedded files require additional merging support.");
            }
            if (graph.Get(names, "Dests") is { } tree)
            {
                Read(tree, 0);
            }
        }
    }

    public PdfValue ResolveDestination(PdfValue raw)
    {
        var seen = new HashSet<PdfValue>();
        var value = graph.Resolve(raw);
        while (seen.Add(value))
        {
            if (value.Type == "array")
            {
                if (value.Items is { Count: > 0 } && selected.Contains(graph.Resolve(value.Items[0])))
                {
                    return value;
                }
                throw new NotSupportedException("A destination refers to an excluded or missing page.");
            }
            if (graph.Get(value, "D") is { } inner)
            {
                value = inner;
            }
            else
            {
                var key = value.Name ?? (value.Type == "string" ? PdfFormGraph.Text(value) : "");
                if (!destinations.TryGetValue(key, out var destination))
                {
                    throw new NotSupportedException("A named destination could not be resolved.");
                }
                value = graph.Resolve(destination);
            }
        }
        throw new InvalidDataException("Cyclic destination.");
    }

    public IEnumerable<(string Name, PdfValue Destination)> RetainedDestinations()
    {
        foreach (var item in destinations)
        {
            PdfValue resolved;
            try
            {
                resolved = ResolveDestination(item.Value);
            }
            catch (NotSupportedException)
            {
                continue;
            }
            yield return (item.Key, resolved);
        }
    }

    public void PrepareOutlines()
    {
        if (graph.Get(catalog, "Outlines") is not { } root)
        {
            return;
        }
        var visited = new HashSet<PdfValue>();
        int Prune(PdfValue parentRaw, int depth)
        {
            var parent = graph.Resolve(parentRaw);
            if (depth > 100 || !visited.Add(parent))
            {
                throw new InvalidDataException("Invalid outline hierarchy.");
            }
            var retained = new List<PdfValue>();
            var visible = 0;
            var current = parent.Entries!.GetValueOrDefault("First");
            while (current is not null)
            {
                var node = graph.Resolve(current);
                var next = node.Entries!.GetValueOrDefault("Next");
                var descendants = Prune(current, depth + 1);
                var destination = node.Entries!.GetValueOrDefault("Dest");
                var action = graph.Get(node, "A");
                destination ??= graph.Get(action ?? Dict(), "S")?.Name == "GoTo" ? graph.Get(action!, "D") : null;
                var keep = true;
                if (destination is not null)
                {
                    try
                    {
                        ResolveDestination(destination);
                    }
                    catch (NotSupportedException)
                    {
                        keep = descendants > 0;
                        node.Entries!.Remove("Dest");
                        node.Entries!.Remove("A");
                    }
                }
                if (keep)
                {
                    retained.Add(current);
                    node.Entries!["Parent"] = parentRaw;
                    visible += 1 + (graph.Number(node, "Count") > 0 ? descendants : 0);
                }
                current = next;
            }
            parent.Entries!.Remove("First");
            parent.Entries!.Remove("Last");
            if (retained.Count > 0)
            {
                parent.Entries["First"] = retained[0];
                parent.Entries["Last"] = retained[^1];
                for (var i = 0; i < retained.Count; i++)
                {
                    var node = graph.Resolve(retained[i]);
                    node.Entries!.Remove("Prev");
                    node.Entries!.Remove("Next");
                    if (i > 0)
                    {
                        node.Entries["Prev"] = retained[i - 1];
                    }
                    if (i + 1 < retained.Count)
                    {
                        node.Entries["Next"] = retained[i + 1];
                    }
                }
            }
            parent.Entries["Count"] = Num(graph.Number(parent, "Count") < 0 ? -visible : visible);
            return visible;
        }
        Prune(catalog.Entries!["Outlines"], 0);
    }

    public void MergeOutlines(PdfObjectDocument output, PdfValue copied)
    {
        var outputCatalog = output.Objects[0].Value;
        if (!outputCatalog.Entries!.TryGetValue("Outlines", out var existing))
        {
            outputCatalog.Entries["Outlines"] = copied;
            return;
        }
        var targetGraph = new PdfGraph(output);
        var root = targetGraph.Resolve(existing);
        var incoming = targetGraph.Resolve(copied);
        if (incoming.Entries!.GetValueOrDefault("First") is not { } first)
        {
            return;
        }
        if (root.Entries!.GetValueOrDefault("Last") is { } last)
        {
            targetGraph.Resolve(last).Entries!["Next"] = first;
            targetGraph.Resolve(first).Entries!["Prev"] = last;
        }
        else
        {
            root.Entries!["First"] = first;
        }
        root.Entries!["Last"] = incoming.Entries!["Last"];
        root.Entries!["Count"] = Num(Math.Abs(targetGraph.Number(root, "Count")) + Math.Abs(targetGraph.Number(incoming, "Count")));
        var current = first;
        while (current is not null)
        {
            var node = targetGraph.Resolve(current);
            node.Entries!["Parent"] = existing;
            current = node.Entries!.GetValueOrDefault("Next");
        }
    }
}
