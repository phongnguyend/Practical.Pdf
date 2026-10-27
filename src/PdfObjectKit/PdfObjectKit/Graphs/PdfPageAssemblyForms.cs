using System.Text;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Graphs;

internal static class PdfPageAssemblyForms
{
    public static PdfValue? Prepare(PdfObjectDocument source, HashSet<PdfValue> selected, int index, HashSet<string> names, bool namespaceFields)
    {
        var form = new PdfFormGraph(source);
        if (form.AcroForm is not { } acro)
        {
            return null;
        }
        if (form.Fields.Any(field => field.Model.IsSigned))
        {
            throw new NotSupportedException("Signed documents cannot be assembled without invalidating their signatures.");
        }
        if (acro.Entries!.Keys.Any(key => key is not ("Fields" or "NeedAppearances" or "SigFlags" or "CO" or "DR" or "DA" or "Q")))
        {
            throw new NotSupportedException("Unknown AcroForm entry has no assembly preservation policy.");
        }
        var graph = form.Graph;
        var selectedWidgets = selected.SelectMany(page => graph.Get(page, "Annots")?.Items ?? []).Select(graph.Resolve).ToHashSet();
        var retained = new HashSet<PdfValue>();
        var visited = new HashSet<PdfValue>();
        bool Keep(PdfValue raw, int depth)
        {
            var node = graph.Resolve(raw);
            if (depth > 100 || !visited.Add(node))
            {
                throw new InvalidDataException("Invalid form hierarchy.");
            }
            if (graph.Get(node, "Kids")?.Items is { } children)
            {
                var kept = children.Where(child => Keep(child, depth + 1)).ToArray();
                if (kept.Length == 0 && children.Count > 0)
                {
                    return false;
                }
                node.Entries!["Kids"] = Array(kept);
            }
            if (graph.Get(node, "Subtype")?.Name == "Widget"
                && !selectedWidgets.Contains(node))
            {
                return false;
            }
            retained.Add(node);
            return true;
        }
        var roots = (graph.Get(acro, "Fields")?.Items ?? []).Where(root => Keep(root, 0)).ToArray();
        acro.Entries!["Fields"] = Array(roots);
        if (graph.Get(acro, "CO")?.Items is { } order)
        {
            acro.Entries["CO"] = Array(order.Where(item => retained.Contains(graph.Resolve(item))).ToArray());
        }
        var renamedRoots = new Dictionary<string, string>();
        foreach (var root in roots)
        {
            var name = graph.Get(root, "T") is { } title ? PdfFormGraph.Text(title) : "";
            if (namespaceFields)
            {
                var replacement = $"Document{index}_{name}";
                renamedRoots.Add(name, replacement);
                graph.Resolve(root).Entries!["T"] = Str(new byte[] { 254, 255 }.Concat(Encoding.BigEndianUnicode.GetBytes(replacement)).ToArray());
                name = replacement;
            }
            if (!names.Add(name))
            {
                throw new NotSupportedException($"Duplicate form root '{name}'. Rename it before merging; scripts and actions may depend on this name.");
            }
        }
        if (namespaceFields || retained.Count != visited.Count)
        {
            var seen = new HashSet<PdfValue>();
            void RewriteFieldNames(PdfValue raw, int depth = 0)
            {
                if (depth > 100)
                {
                    throw new InvalidDataException("Field action traversal exceeds the nesting limit.");
                }
                var node = graph.Resolve(raw);
                if (!seen.Add(node))
                {
                    return;
                }
                if (graph.Get(node, "S")?.Name == "JavaScript" || node.Entries?.ContainsKey("JS") == true)
                {
                    throw new NotSupportedException("Cannot rename or remove form fields in a document containing JavaScript; field references in scripts cannot be rewritten safely.");
                }
                if (graph.Get(node, "Fields") is { Type: "array", Items: { } fields })
                {
                    for (var i = 0; i < fields.Count; i++)
                    {
                        var field = graph.Resolve(fields[i]);
                        if (field.Type != "string")
                        {
                            continue;
                        }
                        var oldName = PdfFormGraph.Text(field);
                        foreach (var root in renamedRoots)
                        {
                            if (oldName == root.Key || oldName.StartsWith(root.Key + ".", StringComparison.Ordinal))
                            {
                                var newName = root.Value + oldName[root.Key.Length..];
                                fields[i] = Str(new byte[] { 254, 255 }.Concat(Encoding.BigEndianUnicode.GetBytes(newName)).ToArray());
                                break;
                            }
                        }
                    }
                }
                foreach (var child in node.Entries?.Values ?? Enumerable.Empty<PdfValue>())
                {
                    RewriteFieldNames(child, depth + 1);
                }
                foreach (var child in node.Items ?? [])
                {
                    RewriteFieldNames(child, depth + 1);
                }
            }
            RewriteFieldNames(source.Trailer);
        }
        var renames = new Dictionary<string, string>();
        var resources = Dict();
        foreach (var category in graph.Get(acro, "DR")?.Entries ?? [])
        {
            if (graph.Resolve(category.Value).Entries is not { } entries)
            {
                resources.Entries![category.Key] = category.Value;
                continue;
            }
            var renamed = Dict();
            foreach (var entry in entries)
            {
                var name = $"Assembly{index}_{entry.Key}";
                renames[entry.Key] = name;
                renamed.Entries![name] = entry.Value;
            }
            resources.Entries![category.Key] = renamed;
        }
        acro.Entries["DR"] = resources;
        var defaultAppearance = graph.Get(acro, "DA");
        foreach (var root in roots)
        {
            var node = graph.Resolve(root);
            if (!node.Entries!.ContainsKey("DA") && defaultAppearance is not null)
            {
                node.Entries["DA"] = defaultAppearance;
            }
            if (!node.Entries.ContainsKey("Q") && graph.Get(acro, "Q") is { } alignment)
            {
                node.Entries["Q"] = alignment;
            }
        }
        foreach (var node in retained)
        {
            if (node.Entries!.TryGetValue("DA", out var appearance))
            {
                var operations = PdfContent.Parse(Convert.FromBase64String(graph.Resolve(appearance).Base64!));
                foreach (var operation in operations)
                {
                    foreach (var operand in operation.Operands)
                    {
                        if (operand.Name is { } name && renames.TryGetValue(name, out var replacement))
                        {
                            operand.Name = replacement;
                        }
                    }
                }
                node.Entries["DA"] = Str(Encoding.Latin1.GetBytes(string.Join(Environment.NewLine, operations.Select(operation =>
                    string.Join(" ", operation.Operands.Select(Syntax)) + " " + operation.Operator))));
            }
        }
        acro.Entries.Remove("DA");
        acro.Entries.Remove("Q");
        return acro;
    }

    public static void Merge(PdfValue catalog, PdfValue copied)
    {
        if (!catalog.Entries!.TryGetValue("AcroForm", out var target))
        {
            catalog.Entries["AcroForm"] = copied;
            return;
        }
        foreach (var key in new[] { "Fields", "CO" })
        {
            if (copied.Entries!.TryGetValue(key, out var list))
            {
                if (!target.Entries!.ContainsKey(key))
                {
                    target.Entries[key] = Array();
                }
                target.Entries[key].Items!.AddRange(list.Items!);
            }
        }
        foreach (var category in copied.Entries!["DR"].Entries!)
        {
            var resources = target.Entries!["DR"];
            if (resources.Entries!.TryGetValue(category.Key, out var existing) && existing.Entries is not null && category.Value.Entries is not null)
            {
                foreach (var resource in category.Value.Entries)
                {
                    existing.Entries[resource.Key] = resource.Value;
                }
            }
            else
            {
                resources.Entries[category.Key] = category.Value;
            }
        }
        if (copied.Entries.GetValueOrDefault("NeedAppearances")?.Boolean == true)
        {
            target.Entries!["NeedAppearances"] = new() { Type = "boolean", Boolean = true };
        }
    }
}
