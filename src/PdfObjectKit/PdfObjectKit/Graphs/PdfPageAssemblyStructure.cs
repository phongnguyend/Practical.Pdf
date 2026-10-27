using System.Text;
using PdfObjectKit.Models;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Graphs;

/// <summary>Prunes the logical structure to selected content and remaps the reverse parent tree.</summary>
internal sealed class PdfPageAssemblyStructure
{
    private readonly PdfGraph graph;
    private readonly HashSet<PdfValue> retained = [];
    private readonly Dictionary<int, PdfValue> parents = [];
    private readonly Dictionary<(int Key, int Mcid), PdfValue> expectedParents = [];
    private readonly Dictionary<int, (int Key, PdfValue Owner, bool Array)> mapped = [];
    private readonly CancellationToken cancellation;
    private readonly int maxNodes;
    private int visitedNodes;

    public PdfValue Root { get; }

    public PdfPageAssemblyStructure(PdfObjectDocument document, IReadOnlySet<PdfValue> selected, int sourceIndex, int maxNodes, CancellationToken cancellation)
    {
        graph = new(document);
        this.cancellation = cancellation;
        this.maxNodes = maxNodes;
        Root = graph.Get(graph.Resolve(document.Trailer.Entries!["Root"]), "StructTreeRoot")!;
        var allowed = new HashSet<string> { "Type", "K", "ParentTree", "ParentTreeNextKey", "RoleMap", "ClassMap", "IDTree", "Namespaces", "AF" };
        if (Root.Type != "dictionary" || Root.Entries!.Keys.Any(key => !allowed.Contains(key)))
        {
            throw new NotSupportedException("Unsupported structure-tree root entry.");
        }
        var seenNumbers = new HashSet<PdfValue>();
        void ReadParents(PdfValue raw, int depth)
        {
            Tick(depth);
            var node = graph.Resolve(raw);
            if (!seenNumbers.Add(node))
            {
                throw new InvalidDataException("Cyclic or repeated structure parent-tree node.");
            }
            var numbers = graph.Get(node, "Nums")?.Items ?? [];
            if (numbers.Count % 2 != 0)
            {
                throw new InvalidDataException("Invalid structure parent-tree pairs.");
            }
            for (var i = 0; i < numbers.Count; i += 2)
            {
                if (!parents.TryAdd(Index(graph.Resolve(numbers[i])), numbers[i + 1]))
                {
                    throw new InvalidDataException("Duplicate structure parent-tree key.");
                }
            }
            foreach (var child in graph.Get(node, "Kids")?.Items ?? [])
            {
                ReadParents(child, depth + 1);
            }
        }
        if (graph.Get(Root, "ParentTree") is { } parentTree)
        {
            ReadParents(parentTree, 0);
        }

        var reachable = new HashSet<PdfValue>();
        void Reach(PdfValue raw, int depth)
        {
            Tick(depth);
            var value = graph.Resolve(raw);
            if (graph.Get(value, "Type")?.Name is "StructElem" or "StructTreeRoot" or "Pages" or "Catalog"
                || graph.Get(value, "Type")?.Name == "Page" && !selected.Contains(value) || !reachable.Add(value))
            {
                return;
            }
            foreach (var entry in value.Entries ?? [])
            {
                if (entry.Key is not ("Parent" or "P" or "StructTreeRoot"))
                {
                    Reach(entry.Value, depth + 1);
                }
            }
            foreach (var item in value.Items ?? [])
            {
                Reach(item, depth + 1);
            }
        }
        foreach (var page in selected)
        {
            Reach(page, 0);
        }
        var seenElements = new HashSet<PdfValue>();
        void Expect(PdfValue owner, int mcid, PdfValue? parent)
        {
            if (parent is null)
            {
                throw new InvalidDataException("Structural content is not inside a structure element.");
            }
            var key = Index(graph.Get(owner, mcid < 0 ? "StructParent" : "StructParents")
                ?? throw new InvalidDataException("Structural content owner has no parent-tree key."));
            if (!parents.TryGetValue(key, out var rawEntry))
            {
                throw new InvalidDataException("Structural content has no reverse parent-tree entry.");
            }
            var entry = graph.Resolve(rawEntry);
            var reverse = mcid < 0 ? entry : entry.Items is { } items && mcid < items.Count ? graph.Resolve(items[mcid]) : null;
            if (!ReferenceEquals(reverse, parent))
            {
                throw new InvalidDataException("Structure content and parent-tree back reference disagree.");
            }
            if (expectedParents.TryGetValue((key, mcid), out var previous) && !ReferenceEquals(previous, parent))
            {
                throw new InvalidDataException("Structural content belongs to multiple elements.");
            }
            expectedParents[(key, mcid)] = parent;
        }
        PdfValue? Prune(PdfValue raw, PdfValue? inheritedPage, int depth, PdfValue? parent = null)
        {
            Tick(depth);
            var node = graph.Resolve(raw);
            if (node.Type == "array")
            {
                return Array(node.Items!.Select(item => Prune(item, inheritedPage, depth + 1, parent)).Where(item => item is not null).Cast<PdfValue>().ToArray());
            }
            if (node.Type == "number")
            {
                _ = Index(node);
                if (inheritedPage is null)
                {
                    throw new InvalidDataException("A structure MCID has no page context.");
                }
                if (!selected.Contains(inheritedPage))
                {
                    return null;
                }
                Expect(inheritedPage, Index(node), parent);
                return raw;
            }
            var page = graph.Get(node, "Pg") ?? inheritedPage;
            var type = graph.Get(node, "Type")?.Name;
            if (type is "MCR" or "OBJR")
            {
                var content = graph.Get(node, type == "MCR" ? "Stm" : "Obj");
                if (type == "MCR")
                {
                    _ = Index(graph.Get(node, "MCID") ?? throw new InvalidDataException("MCR has no MCID."));
                }
                if (page is not null && !selected.Contains(page) || content is not null && !reachable.Contains(content))
                {
                    return null;
                }
                if (page is null && content is null)
                {
                    throw new InvalidDataException("Structure content reference has no page or object.");
                }
                Expect(content ?? page!, type == "OBJR" ? -1 : Index(graph.Get(node, "MCID")!), parent);
                return raw;
            }
            if (type != "StructElem" && graph.Get(node, "S") is null || raw.Reference is null || !seenElements.Add(node))
            {
                throw new InvalidDataException("Invalid, repeated or direct structure element.");
            }
            var child = node.Entries!.GetValueOrDefault("K");
            var kept = child is null ? null : Prune(child, page, depth + 1, node);
            if (kept is null || kept.Type == "array" && kept.Items!.Count == 0)
            {
                return null;
            }
            node.Entries!["K"] = kept;
            if (graph.Get(node, "Pg") is { } ownPage && !selected.Contains(ownPage))
            {
                node.Entries.Remove("Pg");
            }
            retained.Add(node);
            return raw;
        }
        // Preserve the raw references: resolving an element before pruning loses its identity.
        var keptRoot = Root.Entries!.TryGetValue("K", out var rawKids) ? Prune(rawKids, null, 0) : null;
        Root.Entries["K"] = keptRoot is null ? Array() : graph.Resolve(keptRoot).Type == "array" ? keptRoot : Array(keptRoot);

        var roles = graph.Get(Root, "RoleMap");
        var classes = graph.Get(Root, "ClassMap");
        var roleNames = roles?.Entries?.Keys.ToDictionary(key => key, key => $"Assembly{sourceIndex}_{key}") ?? [];
        var classNames = classes?.Entries?.Keys.ToDictionary(key => key, key => $"Assembly{sourceIndex}_{key}") ?? [];
        if (roles?.Entries is { } roleEntries)
        {
            Root.Entries["RoleMap"] = Dict(roleEntries.Select(pair => (roleNames[pair.Key],
                graph.Resolve(pair.Value).Name is { } name && roleNames.TryGetValue(name, out var renamed) ? Name(renamed) : pair.Value)).ToArray());
        }
        if (classes?.Entries is { } classEntries)
        {
            Root.Entries["ClassMap"] = Dict(classEntries.Select(pair => (classNames[pair.Key], pair.Value)).ToArray());
        }
        var objectIds = document.Objects.ToDictionary(item => item.Value, item => item.Id);
        var ids = new List<(PdfValue Id, PdfValue Reference)>();
        var idKeys = new HashSet<string>();
        foreach (var node in retained)
        {
            if (graph.Get(node, "NS") is null && graph.Get(node, "S")?.Name is { } role && roleNames.TryGetValue(role, out var renamedRole))
            {
                node.Entries!["S"] = Name(renamedRole);
            }
            if (graph.Get(node, "C") is { } classValue)
            {
                PdfValue RenameClass(PdfValue item) => graph.Resolve(item).Name is { } name && classNames.TryGetValue(name, out var renamed) ? Name(renamed) : item;
                node.Entries!["C"] = classValue.Type == "array" ? Array(classValue.Items!.Select(RenameClass).ToArray()) : RenameClass(classValue);
            }
            if (graph.Get(node, "Ref")?.Items is { } refs)
            {
                node.Entries!["Ref"] = Array(refs.Where(item => retained.Contains(graph.Resolve(item))).ToArray());
            }
            if (graph.Get(node, "ID") is { } id)
            {
                var bytes = Encoding.ASCII.GetBytes($"Assembly{sourceIndex}:").Concat(Convert.FromBase64String(id.Base64!)).ToArray();
                if (!idKeys.Add(Convert.ToHexString(bytes)))
                {
                    throw new InvalidDataException("Duplicate structure element ID.");
                }
                var updated = Str(bytes);
                node.Entries!["ID"] = updated;
                ids.Add((updated, Ref(objectIds[node])));
            }
        }
        var seenAttributes = new HashSet<PdfValue>();
        void RewriteHeaders(PdfValue raw, int depth)
        {
            Tick(depth);
            var value = graph.Resolve(raw);
            if (!seenAttributes.Add(value))
            {
                return;
            }
            if (value.Entries?.TryGetValue("Headers", out var rawHeaders) == true)
            {
                var headers = graph.Resolve(rawHeaders);
                if (headers.Type == "array")
                {
                    var rewritten = new List<PdfValue>();
                    foreach (var item in headers.Items!)
                    {
                        var id = Encoding.ASCII.GetBytes($"Assembly{sourceIndex}:").Concat(Convert.FromBase64String(graph.Resolve(item).Base64!)).ToArray();
                        if (!idKeys.Contains(Convert.ToHexString(id)))
                        {
                            throw new NotSupportedException("A table header refers to a structure element outside the selected pages.");
                        }
                        rewritten.Add(Str(id));
                    }
                    value.Entries["Headers"] = Array(rewritten.ToArray());
                }
            }
            foreach (var item in value.Items ?? [])
            {
                if (graph.Resolve(item).Type is "dictionary" or "array")
                {
                    RewriteHeaders(item, depth + 1);
                }
            }
        }
        foreach (var node in retained)
        {
            if (graph.Get(node, "A") is { } attributes)
            {
                RewriteHeaders(attributes, 0);
            }
        }
        foreach (var attributes in graph.Get(Root, "ClassMap")?.Entries?.Values ?? Enumerable.Empty<PdfValue>())
        {
            RewriteHeaders(attributes, 0);
        }
        Root.Entries.Remove("IDTree");
        if (ids.Count > 0)
        {
            Root.Entries["IDTree"] = Dict(("Names", Array(ids.OrderBy(item => Convert.ToHexString(Convert.FromBase64String(item.Id.Base64!)), StringComparer.Ordinal)
                .SelectMany(item => new[] { item.Id, item.Reference }).ToArray())));
        }
        Root.Entries.Remove("ParentTree");
        Root.Entries.Remove("ParentTreeNextKey");
    }

    private void Tick(int depth)
    {
        cancellation.ThrowIfCancellationRequested();
        if (depth > 100 || ++visitedNodes > maxNodes)
        {
            throw new InvalidDataException("Structure traversal exceeds assembly limits.");
        }
    }

    private static int Index(PdfValue value)
    {
        var number = Number(value);
        if (value.Type != "number" || number < 0 || number > int.MaxValue || number != Math.Truncate(number))
        {
            throw new InvalidDataException("Invalid structure index.");
        }
        return (int)number;
    }

    public PdfValue MapParent(PdfValue owner, PdfValue raw, bool array, Func<int> allocate)
    {
        var old = Index(graph.Resolve(raw));
        if (mapped.TryGetValue(old, out var existing))
        {
            if (!ReferenceEquals(existing.Owner, owner) || existing.Array != array)
            {
                throw new InvalidDataException("Structure parent key is shared by different objects.");
            }
            return Num(existing.Key);
        }
        if (!parents.ContainsKey(old))
        {
            throw new InvalidDataException("Missing structure parent-tree entry.");
        }
        var key = allocate();
        mapped.Add(old, (key, owner, array));
        return Num(key);
    }

    public void AppendParents(PdfValue outputRoot, Func<PdfValue, PdfValue> copy)
    {
        var output = outputRoot.Entries!["ParentTree"].Entries!["Nums"].Items!;
        foreach (var pair in mapped.OrderBy(item => item.Value.Key))
        {
            var value = graph.Resolve(parents[pair.Key]);
            PdfValue Filter(PdfValue item, int mcid)
            {
                var resolved = graph.Resolve(item);
                if (resolved.Type == "null" || !retained.Contains(resolved))
                {
                    return new() { Type = "null" };
                }
                if (!expectedParents.TryGetValue((pair.Key, mcid), out var expected) || !ReferenceEquals(expected, resolved))
                {
                    throw new InvalidDataException("Parent-tree entry does not correspond to retained structural content.");
                }
                return copy(item);
            }
            if (pair.Value.Array != (value.Type == "array"))
            {
                throw new InvalidDataException("Structure parent-tree entry has an incorrect type.");
            }
            var copied = value.Type == "array" ? Array(value.Items!.Select((item, mcid) => Filter(item, mcid)).ToArray()) : Filter(parents[pair.Key], -1);
            if (copied.Type == "null" && !pair.Value.Array)
            {
                throw new InvalidDataException("Selected object has no retained structure parent.");
            }
            output.Add(Num(pair.Value.Key));
            output.Add(copied);
        }
    }
}
