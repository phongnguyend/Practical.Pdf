using System.Security.Cryptography;
using System.Text;
using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

internal static class PdfTextRunRewriter
{
    private sealed record State(PdfMatrix Matrix, int RenderingMode = 0);

    public static void Apply(PdfObjectDocument document, int pageNumber, IReadOnlyList<PdfTextRun> runs,
        Func<PdfGraph, NativePage, PdfPageInterpreter, HashSet<(PdfReference StreamObject, int? FormOccurrenceId, int OperatorIndex, int OperandIndex)>, PdfTextRewriteActions> prepare)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
        {
            throw new ArgumentException("Choose at least one text run.");
        }

        PdfPageEditor.Edit(document, working =>
        {
            var graph = new PdfGraph(working);
            var pages = graph.Pages();
            if (pageNumber < 1 || pageNumber > pages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pageNumber));
            }
            var page = pages[pageNumber - 1];
            var parsed = new PdfPageInterpreter(graph, page);
            if (parsed.TextWarning is not null)
            {
                throw new NotSupportedException(parsed.TextWarning);
            }
            var targets = runs.Select(run => (run.StreamObject, run.FormOccurrenceId, run.OperatorIndex, run.OperandIndex)).ToHashSet();
            foreach (var run in runs)
            {
                if (!parsed.Slots.Any(slot => slot.Stream == run.StreamObject && slot.FormOccurrenceId == run.FormOccurrenceId && slot.Operation == run.OperatorIndex && slot.Token == run.OperandIndex)
                    || !parsed.TextStreams.TryGetValue(run.StreamObject, out var stream)
                    || Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(stream.Base64!))) != run.StreamSha256)
                {
                    throw new InvalidDataException("Text selection is stale or invalid. Reacquire the runs before editing.");
                }
            }

            PdfValue Add(PdfValue value)
            {
                var id = new PdfReference(working.Objects.Max(item => item.Id.ObjectNumber) + 1);
                working.Objects.Add(new(id, value));
                return Ref(id);
            }
            var actions = prepare(graph, page, parsed, targets);
            var formCount = 0;
            var rewritten = 0;
            (PdfValue Stream, PdfValue Resources, bool Changed) Rewrite(PdfValue source, PdfReference id, PdfValue resources, int? occurrence, int offset, ref State state, Stack<State> stack, int depth)
            {
                if (depth > 100)
                {
                    throw new InvalidDataException("Form nesting exceeds limit.");
                }
                var operations = PdfContent.Parse(PdfStreamDecoder.Decode(source));
                var plan = actions.Plan(id, occurrence, offset);
                var output = new StringBuilder();
                var changed = false;
                var copiedResources = Dict();
                copiedResources.Entries = new(resources.Entries!);
                var xobjects = Dict();
                xobjects.Entries = new(graph.Get(resources, "XObject")?.Entries ?? []);
                for (var i = 0; i < operations.Count; i++)
                {
                    var op = operations[i];
                    var grouped = plan.Scopes.Any(scope => scope.Start <= i && scope.End >= i);
                    if (op.Operator == "q")
                    {
                        stack.Push(state);
                    }
                    else if (op.Operator == "Q")
                    {
                        state = stack.Pop();
                    }
                    else if (op.Operator == "cm")
                    {
                        state = state with { Matrix = state.Matrix.Multiply(PdfMatrix.From(op.Operands)) };
                    }
                    else if (op.Operator == "Tr")
                    {
                        state = state with { RenderingMode = (int)Number(op.Operands[0]) };
                    }
                    if (op.Operator == "Do")
                    {
                        var raw = graph.Get(resources, "XObject")?.Entries?.GetValueOrDefault(op.Operands[0].Name!);
                        if (raw is not null && graph.Get(raw, "Subtype")?.Name == "Form")
                        {
                            var form = graph.Resolve(raw);
                            var childState = state with { Matrix = state.Matrix.Multiply(graph.Get(form, "Matrix")?.Items is { } matrix ? PdfMatrix.From(matrix) : PdfMatrix.Identity) };
                            var nested = Rewrite(form, raw.Reference!, graph.Get(form, "Resources") ?? resources, ++formCount, 0, ref childState, new(), depth + 1);
                            if (nested.Changed && plan.Forms.Contains(i))
                            {
                                // Use the original Form: its selected text and clip move as one invocation.
                                if (!grouped)
                                {
                                    output.Append(actions.Transform(state.Matrix, 1));
                                }
                                output.Append(op.Serialize());
                                if (!grouped)
                                {
                                    output.Append(actions.Transform(state.Matrix, -1));
                                }
                                changed = true;
                                continue;
                            }
                            if (nested.Changed)
                            {
                                var name = "PdfObjectKitMovedText";
                                while (xobjects.Entries!.ContainsKey(name))
                                {
                                    name += "_";
                                }
                                nested.Stream.Entries!["Resources"] = nested.Resources;
                                xobjects.Entries![name] = Add(nested.Stream);
                                op = new("Do", [Name(name)]);
                                changed = true;
                            }
                        }
                    }

                    var tokens = targets.Where(key => key.StreamObject == id && key.FormOccurrenceId == occurrence && key.OperatorIndex == i + offset).Select(key => key.OperandIndex).ToHashSet();
                    if (tokens.Count > 0)
                    {
                        if (state.RenderingMode >= 4)
                        {
                            throw new NotSupportedException("Editing text used as a clipping path is not supported.");
                        }
                        var textMatrix = state.Matrix;
                        void Show(string command, bool selected, int token = -1)
                        {
                            if (selected)
                            {
                                var slot = parsed.Slots.Single(slot => slot.Stream == id && slot.FormOccurrenceId == occurrence && slot.Operation == i + offset && slot.Token == token);
                                output.Append(actions.RewriteSelected(command, slot, textMatrix, grouped));
                                rewritten++;
                            }
                            else
                            {
                                output.Append(command);
                            }
                        }
                        if (op.Operator == "TJ")
                        {
                            var items = op.Operands[0].Items!;
                            for (var token = 0; token < items.Count; token++)
                            {
                                Show($"[{Syntax(items[token])}] TJ\n", tokens.Contains(token), token);
                            }
                        }
                        else
                        {
                            if (op.Operator == "\"")
                            {
                                output.Append($"{Syntax(op.Operands[0])} Tw {Syntax(op.Operands[1])} Tc\n");
                            }
                            if (op.Operator is "'" or "\"")
                            {
                                output.Append("T*\n");
                            }
                            Show($"{Syntax(op.Operands.Last())} Tj\n", true);
                        }
                        changed = true;
                    }
                    else if (op.InlineImage is { } inline)
                    {
                        output.Append("BI\n");
                        foreach (var entry in inline.Entries!)
                        {
                            output.Append(Syntax(Name(entry.Key))).Append(' ').Append(Syntax(entry.Value)).Append('\n');
                        }
                        output.Append("ID\n").Append(Encoding.Latin1.GetString(Convert.FromBase64String(inline.Base64!))).Append("\nEI\n");
                    }
                    else
                    {
                        output.Append(op.Serialize());
                    }
                    if (plan.Scopes.Any(scope => scope.Start == i))
                    {
                        // Insert inside the existing q/Q pair, before its clipping path is established.
                        output.Append(actions.Transform(state.Matrix, 1));
                    }
                }
                var result = Stream(Encoding.Latin1.GetBytes(output.ToString()));
                result.Entries = new(source.Entries!);
                result.Entries.Remove("Filter");
                result.Entries.Remove("DecodeParms");
                result.Entries.Remove("Length");
                copiedResources.Entries!["XObject"] = xobjects;
                return (result, copiedResources, changed);
            }

            var state = new State(PdfMatrix.Identity);
            var stack = new Stack<State>();
            var offset = 0;
            var contents = new List<PdfValue>();
            var resources = page.Resources;
            foreach (var stream in page.Streams)
            {
                var result = Rewrite(stream.Value, stream.Id, resources, null, offset, ref state, stack, 0);
                offset += PdfContent.Parse(PdfStreamDecoder.Decode(stream.Value)).Count;
                contents.Add(result.Changed ? Add(result.Stream) : Ref(stream.Id));
                resources = result.Resources;
            }
            if (rewritten != targets.Count)
            {
                throw new InvalidDataException("Not all selected text runs could be edited.");
            }
            page.Dictionary.Entries!["Contents"] = Array(contents.ToArray());
            page.Dictionary.Entries["Resources"] = resources;
        });
    }
}

// Strategies choose which isolated groups travel together and how selected operands change.
internal sealed record PdfTextRewritePlan(bool Exclusive, HashSet<int> Forms, List<(int Start, int End)> Scopes)
{
    public static PdfTextRewritePlan Empty { get; } = new(false, [], []);
}

internal sealed record PdfTextRewriteActions(
    Func<PdfReference, int?, int, PdfTextRewritePlan> Plan,
    Func<PdfMatrix, double, string> Transform,
    Func<string, NativeSlot, PdfMatrix, bool, string> RewriteSelected);
