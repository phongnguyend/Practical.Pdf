using PdfObjectKit.Models;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Graphs;

/// <summary>Converts annotation object references to marked content in appearance wrappers.</summary>
internal sealed class PdfFlattenStructure
{
    private readonly PdfGraph graph;
    private readonly PdfValue? root;
    private readonly SortedDictionary<int, PdfValue> parents = [];
    private readonly Dictionary<PdfValue, (PdfValue Node, PdfValue Parent)> references = [];
    private int next;

    public PdfFlattenStructure(PdfGraph graph, PdfValue catalog)
    {
        this.graph = graph;
        root = graph.Get(catalog, "StructTreeRoot");
        if (root is null)
        {
            return;
        }
        var seen = new HashSet<PdfValue>();
        void Read(PdfValue raw, int depth)
        {
            var node = graph.Resolve(raw);
            if (depth > 100 || !seen.Add(node))
            {
                throw new InvalidDataException("Invalid structure parent tree.");
            }
            var nums = graph.Get(node, "Nums")?.Items ?? [];
            if (nums.Count % 2 != 0)
            {
                throw new InvalidDataException("Invalid parent-tree pairs.");
            }
            for (var i = 0; i < nums.Count; i += 2)
            {
                if (!parents.TryAdd(Index(graph.Resolve(nums[i])), nums[i + 1]))
                {
                    throw new InvalidDataException("Duplicate parent-tree key.");
                }
            }
            foreach (var kid in graph.Get(node, "Kids")?.Items ?? [])
            {
                Read(kid, depth + 1);
            }
        }
        if (graph.Get(root, "ParentTree") is { } tree)
        {
            Read(tree, 0);
        }
        next = Math.Max(parents.Count == 0 ? 0 : checked(parents.Keys.Max() + 1), graph.Get(root, "ParentTreeNextKey") is { } key ? Index(key) : 0);
        seen.Clear();
        void Visit(PdfValue raw, PdfValue? parent, int depth)
        {
            var node = graph.Resolve(raw);
            if (depth > 100)
            {
                throw new InvalidDataException("Structure tree is too deep.");
            }
            if (node.Type == "array")
            {
                foreach (var child in node.Items!)
                {
                    Visit(child, parent, depth + 1);
                }
                return;
            }
            if (node.Type != "dictionary")
            {
                return;
            }
            if (!seen.Add(node))
            {
                throw new InvalidDataException("Repeated structure content reference.");
            }
            if (graph.Get(node, "Type")?.Name == "OBJR")
            {
                var target = graph.Get(node, "Obj") ?? throw new InvalidDataException("OBJR has no object.");
                if (parent?.Reference is null || !references.TryAdd(target, (node, parent)))
                {
                    throw new NotSupportedException("Annotation structure must have one indirect parent element.");
                }
                return;
            }
            if (node.Entries!.TryGetValue("K", out var children))
            {
                Visit(children, raw, depth + 1);
            }
        }
        if (root.Entries!.TryGetValue("K", out var kids))
        {
            Visit(kids, null, 0);
        }
    }

    public bool IsTagged(PdfValue annotation) => references.ContainsKey(annotation) || graph.Get(annotation, "StructParent") is not null;

    public int? Convert(PdfValue annotation, PdfValue page, PdfValue appearance)
    {
        if (!references.TryGetValue(annotation, out var entry))
        {
            if (graph.Get(annotation, "StructParent") is not null)
            {
                throw new InvalidDataException("Tagged annotation has no matching OBJR.");
            }
            return null;
        }
        var oldKey = Index(graph.Get(annotation, "StructParent") ?? throw new InvalidDataException("Tagged annotation has no parent-tree key."));
        if (!parents.TryGetValue(oldKey, out var oldParent) || !ReferenceEquals(graph.Resolve(oldParent), graph.Resolve(entry.Parent)))
        {
            throw new InvalidDataException("Annotation and structure parent-tree reference disagree.");
        }
        var key = next;
        next = checked(next + 1);
        parents.Remove(oldKey);
        parents.Add(key, Array(entry.Parent));
        entry.Node.Entries = Dict(("Type", Name("MCR")), ("Pg", page), ("Stm", appearance), ("MCID", Num(0))).Entries;
        return key;
    }

    public void Finish()
    {
        if (root is not null)
        {
            root.Entries!["ParentTree"] = Dict(("Nums", Array(parents.SelectMany(pair => new[] { Num(pair.Key), pair.Value }).ToArray())));
            root.Entries["ParentTreeNextKey"] = Num(next);
        }
    }

    private static int Index(PdfValue value)
    {
        var number = Number(value);
        if (number < 0 || number >= int.MaxValue || number != Math.Truncate(number))
        {
            throw new InvalidDataException("Invalid structure parent-tree index.");
        }
        return (int)number;
    }
}
