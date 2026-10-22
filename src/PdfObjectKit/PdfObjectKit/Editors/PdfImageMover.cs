using System.Text;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

internal static class PdfImageMover
{
    // Only graphics-state and unpainted path operations are safe to move with an image.
    private static readonly HashSet<string> StateOperators = ["q", "Q", "cm", "gs", "w", "J", "j", "M", "d", "ri", "i", "CS", "cs", "SC", "SCN", "sc", "scn", "G", "g", "RG", "rg", "K", "k", "m", "l", "c", "v", "y", "h", "re", "W", "W*", "n", "BMC", "BDC", "EMC", "MP", "DP"];

    public static void Move(PdfObjectDocument document, int pageNumber, int imageNumber, double dx, double dy)
    {
        PdfPageEditor.Edit(document, working =>
        {
            var indexed = PdfImageExtractor.Extract(working)[pageNumber - 1];
            if (indexed.Warning is not null || !indexed.Images.Any(image => image.ImageNumber == imageNumber))
            {
                throw new NotSupportedException("Image movement requires a complete image index and a valid occurrence number.");
            }

            var graph = new PdfGraph(working);
            var page = graph.Pages()[pageNumber - 1];
            var count = 0;
            PdfValue Add(PdfValue value)
            {
                var id = new PdfReference(working.Objects.Max(item => item.Id.ObjectNumber) + 1);
                working.Objects.Add(new(id, value));
                return Ref(id);
            }

            (PdfValue Stream, PdfValue Resources, bool Found, bool Exclusive) Rewrite(PdfValue source, PdfValue resources, PdfMatrix initial, int depth)
            {
                if (depth > 100)
                {
                    throw new InvalidDataException("Form nesting exceeds limit.");
                }

                var operations = PdfContent.Parse(PdfStreamDecoder.Decode(source)).ToList();
                var matrices = new List<PdfMatrix>();
                var stack = new Stack<(int Index, PdfMatrix Matrix)>();
                var scopes = new List<(int Start, int End)>();
                var matrix = initial;
                var selected = -1;
                var found = false;
                var updatedResources = Dict();
                updatedResources.Entries = new(resources.Entries!);
                var xobjects = Dict();
                xobjects.Entries = new(graph.Get(resources, "XObject")?.Entries ?? []);
                for (var i = 0; i < operations.Count; i++)
                {
                    var op = operations[i];
                    matrices.Add(matrix);
                    if (op.Operator == "q")
                    {
                        stack.Push((i, matrix));
                    }
                    else if (op.Operator == "Q" && stack.Count > 0)
                    {
                        var saved = stack.Pop();
                        scopes.Add((saved.Index, i));
                        matrix = saved.Matrix;
                    }
                    else if (op.Operator == "cm")
                    {
                        matrix = matrix.Multiply(PdfMatrix.From(op.Operands));
                    }

                    var target = op.Operator == "Do" ? graph.Get(resources, "XObject")?.Entries?.GetValueOrDefault(op.Operands[0].Name!) : null;
                    var subtype = target is null ? null : graph.Get(target, "Subtype")?.Name;
                    if (op.Operator == "BI" || subtype == "Image")
                    {
                        if (++count == imageNumber)
                        {
                            selected = i;
                            found = true;
                        }
                    }
                    else if (subtype == "Form")
                    {
                        var form = graph.Resolve(target!);
                        var formMatrix = graph.Get(form, "Matrix")?.Items is { } values ? PdfMatrix.From(values) : PdfMatrix.Identity;
                        var nested = Rewrite(form, graph.Get(form, "Resources") ?? resources, matrix.Multiply(formMatrix), depth + 1);
                        if (nested.Found)
                        {
                            found = true;
                            if (nested.Exclusive)
                            {
                                // Translate the invocation, so its BBox and internal clipping travel too.
                                selected = i;
                            }
                            else
                            {
                                var name = "PdfObjectKitMovedForm";
                                while (xobjects.Entries!.ContainsKey(name))
                                {
                                    name += "_";
                                }
                                nested.Stream.Entries!["Resources"] = nested.Resources;
                                xobjects.Entries![name] = Add(nested.Stream);
                                operations[i] = new("Do", [Name(name)]);
                            }
                        }
                    }
                }

                bool OnlySelected(int start, int end) => Enumerable.Range(start, end - start + 1).All(i => i == selected || StateOperators.Contains(operations[i].Operator));
                var exclusive = selected >= 0 && OnlySelected(0, operations.Count - 1);
                var begin = selected;
                var end = selected;
                if (selected >= 0)
                {
                    foreach (var scope in scopes.OrderBy(scope => scope.Start))
                    {
                        if (scope.Start < selected && scope.End > selected && OnlySelected(scope.Start, scope.End))
                        {
                            begin = scope.Start;
                            end = scope.End;
                            break;
                        }
                    }
                }

                var output = new StringBuilder();
                for (var i = 0; i < operations.Count; i++)
                {
                    if (i == begin)
                    {
                        var transform = matrices[i];
                        var determinant = transform.A * transform.D - transform.B * transform.C;
                        var x = (transform.D * dx - transform.C * dy) / determinant;
                        var y = (transform.A * dy - transform.B * dx) / determinant;
                        if (!double.IsFinite(x) || !double.IsFinite(y))
                        {
                            throw new NotSupportedException("Cannot move a degenerate image placement.");
                        }
                        output.Append($"q 1 0 0 1 {Syntax(Num(x))} {Syntax(Num(y))} cm\n");
                    }
                    var op = operations[i];
                    if (op.InlineImage is { } inline)
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
                    if (i == end)
                    {
                        output.Append("Q\n");
                    }
                }
                var result = Stream(Encoding.Latin1.GetBytes(output.ToString()));
                result.Entries = new(source.Entries!);
                result.Entries.Remove("Filter");
                result.Entries.Remove("DecodeParms");
                result.Entries.Remove("Length");
                updatedResources.Entries!["XObject"] = xobjects;
                return (result, updatedResources, found, exclusive);
            }

            var source = Stream(Encoding.Latin1.GetBytes(string.Join("\n", page.Streams.Select(stream => Encoding.Latin1.GetString(PdfStreamDecoder.Decode(stream.Value))))));
            var result = Rewrite(source, page.Resources, PdfMatrix.Identity, 0);
            if (!result.Found)
            {
                throw new InvalidDataException("Image occurrence could not be located.");
            }
            page.Dictionary.Entries!["Contents"] = Add(result.Stream);
            page.Dictionary.Entries["Resources"] = result.Resources;
        });
    }
}
