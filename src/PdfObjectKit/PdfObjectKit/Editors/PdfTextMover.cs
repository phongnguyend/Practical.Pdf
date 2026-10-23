using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

internal static class PdfTextMover
{
    private static readonly HashSet<string> NonPaintingOperators = ["q", "Q", "cm", "gs", "w", "J", "j", "M", "d", "ri", "i", "CS", "cs", "SC", "SCN", "sc", "scn", "G", "g", "RG", "rg", "K", "k", "m", "l", "c", "v", "y", "h", "re", "W", "W*", "n", "BT", "ET", "Tf", "Tc", "Tw", "Tz", "TL", "Tr", "Ts", "Td", "TD", "Tm", "T*", "BMC", "BDC", "EMC", "MP", "DP"];

    public static void Move(PdfObjectDocument document, int pageNumber, IReadOnlyList<PdfTextRun> runs, double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
        {
            throw new ArgumentException("Choose finite movement coordinates.");
        }
        PdfTextRunRewriter.Apply(document, pageNumber, runs, (graph, page, parsed, targets) =>
        {
            // Identify groups containing only selected text before modifying any stream.
            // Lift their movement above local clips and, for exclusive Forms, above the BBox.
            var plans = new Dictionary<(PdfReference, int?, int), PdfTextRewritePlan>();
            var plannedForms = 0;
            bool Plan(PdfValue source, PdfReference id, PdfValue resources, int? occurrence, int offset, int depth)
            {
                if (depth > 100)
                {
                    throw new InvalidDataException("Form nesting exceeds limit.");
                }
                var operations = PdfContent.Parse(PdfStreamDecoder.Decode(source));
                var eligible = new bool[operations.Count];
                var selected = new bool[operations.Count];
                var forms = new HashSet<int>();
                var scopes = new List<(int Start, int End)>();
                var stack = new Stack<int>();
                for (var i = 0; i < operations.Count; i++)
                {
                    var op = operations[i];
                    eligible[i] = NonPaintingOperators.Contains(op.Operator);
                    if (op.Operator is "Tj" or "TJ" or "'" or "\"")
                    {
                        var slots = parsed.Slots.Where(slot => slot.Stream == id && slot.FormOccurrenceId == occurrence && slot.Operation == i + offset).ToList();
                        // Paragraph extraction omits standalone whitespace runs. They advance
                        // the cursor but must not pin a selected line's clipping region in place.
                        selected[i] = slots.Any(slot => targets.Contains((id, occurrence, slot.Operation, slot.Token)));
                        var onlySelectedOrWhitespace = slots.Count > 0 && slots.All(slot => string.IsNullOrWhiteSpace(slot.Text)
                            || targets.Contains((id, occurrence, slot.Operation, slot.Token)));
                        // A TJ containing only spacing adjustments paints nothing. Rewrites may
                        // produce these operations; they must not prevent moving the local clip.
                        eligible[i] = onlySelectedOrWhitespace || (op.Operator == "TJ" && op.Operands[0].Items!.All(item => item.Type == "number"));
                    }
                    else if (op.Operator == "Do")
                    {
                        var raw = graph.Get(resources, "XObject")?.Entries?.GetValueOrDefault(op.Operands[0].Name!);
                        if (raw is not null && graph.Get(raw, "Subtype")?.Name == "Form")
                        {
                            var form = graph.Resolve(raw);
                            eligible[i] = selected[i] = Plan(form, raw.Reference!, graph.Get(form, "Resources") ?? resources, ++plannedForms, 0, depth + 1);
                            if (selected[i])
                            {
                                forms.Add(i);
                            }
                        }
                    }
                    if (op.Operator == "q")
                    {
                        stack.Push(i);
                    }
                    else if (op.Operator == "Q" && stack.Count > 0)
                    {
                        var start = stack.Pop();
                        if (Enumerable.Range(start, i - start + 1).All(n => eligible[n]) && Enumerable.Range(start, i - start + 1).Any(n => selected[n]))
                        {
                            scopes.Add((start, i));
                        }
                    }
                }
                // Only keep outermost eligible scopes; nested translations would move twice.
                var outer = scopes.Where(scope => !scopes.Any(parent => parent.Start < scope.Start && parent.End > scope.End)).ToList();
                var exclusive = eligible.All(value => value) && selected.Any(value => value);
                plans[(id, occurrence, offset)] = new(exclusive, forms, outer);
                return exclusive;
            }
            var plannedOffset = 0;
            foreach (var stream in page.Streams)
            {
                Plan(stream.Value, stream.Id, page.Resources, null, plannedOffset, 0);
                plannedOffset += PdfContent.Parse(PdfStreamDecoder.Decode(stream.Value)).Count;
            }

            string Translation(PdfMatrix matrix, double sign = 1)
            {
                var determinant = matrix.A * matrix.D - matrix.B * matrix.C;
                var x = (matrix.D * dx - matrix.C * dy) / determinant * sign;
                var y = (matrix.A * dy - matrix.B * dx) / determinant * sign;
                if (!double.IsFinite(x) || !double.IsFinite(y))
                {
                    throw new NotSupportedException("Cannot move text with a degenerate transform.");
                }
                return $"1 0 0 1 {Syntax(Num(x))} {Syntax(Num(y))} cm\n";
            }

            // Undo only the translation: q/Q can restore the renderer's text cursor
            // and lose the advance needed to position subsequent runs.
            return new PdfTextRewriteActions(
                (id, occurrence, offset) => plans[(id, occurrence, offset)],
                Translation,
                (command, _, matrix, grouped) => grouped ? command : Translation(matrix) + command + Translation(matrix, -1));
        });
    }
}
