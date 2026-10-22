using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using PdfObjectKit.Validators;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

/// <summary>Atomic page content edits. Re-extract selections after every change.</summary>
public static class PdfPageEditor
{
    /// <summary>Inserts black, single-line ASCII Helvetica text at a PDF-space baseline.</summary>
    public static void InsertText(PdfObjectDocument document, int pageNumber, string text, double x, double y, double fontSize = 12)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Any(c => c < 32 || c > 126) || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(fontSize) || fontSize <= 0)
        {
            throw new ArgumentException("Text must be printable ASCII with finite coordinates and positive font size.");
        }

        Edit(document, working =>
        {
            var graph = new PdfGraph(working);
            var page = graph.Pages()[pageNumber - 1];
            PdfImageEditor.ValidateContent(page);
            var resources = CopyDictionary(page.Resources);
            var fonts = CopyDictionary(graph.Get(resources, "Font") ?? Dict());
            var name = Unique(fonts, "PdfObjectKitText");
            fonts.Entries![name] = Add(working, Dict(("Type", Name("Font")), ("Subtype", Name("Type1")), ("BaseFont", Name("Helvetica"))));
            resources.Entries!["Font"] = fonts;
            page.Dictionary.Entries!["Resources"] = resources;
            var original = string.Join("\n", page.Streams.Select(s => Encoding.Latin1.GetString(PdfStreamDecoder.Decode(s.Value))));
            var commands = $"q\n{original}\nQ\nn\nq BT /{name} {Syntax(Num(fontSize))} Tf 0 g 1 0 0 1 {Syntax(Num(x))} {Syntax(Num(y))} Tm {Syntax(Str(Encoding.ASCII.GetBytes(text)))} Tj ET Q\n";
            page.Dictionary.Entries["Contents"] = Add(working, Stream(Encoding.Latin1.GetBytes(commands)));
        });
    }

    /// <summary>Combines a page's content streams without modifying shared source streams.</summary>
    public static void ConsolidateContent(PdfObjectDocument document, int pageNumber)
    {
        Edit(document, working =>
        {
            var page = new PdfGraph(working).Pages()[pageNumber - 1];
            var content = string.Join("\n", page.Streams.Select(s => Encoding.Latin1.GetString(PdfStreamDecoder.Decode(s.Value))));
            page.Dictionary.Entries!["Contents"] = Add(working, Stream(Encoding.Latin1.GetBytes(content)));
        });
    }

    /// <summary>Removes one drawn image occurrence, cloning any shared Form invocation on its path.</summary>
    public static void DeleteImage(PdfObjectDocument document, int pageNumber, int imageNumber)
    {
        RewriteImage(document, pageNumber, imageNumber, null, PdfImageFit.Stretch);
    }

    internal static void RewriteImage(PdfObjectDocument document, int pageNumber, int imageNumber, PdfImageData? replacement, PdfImageFit fit, double? deltaX = null, double deltaY = 0)
    {
        Edit(document, working =>
        {
            var indexed = PdfImageExtractor.Extract(working)[pageNumber - 1];
            if (indexed.Warning is not null || !indexed.Images.Any(i => i.ImageNumber == imageNumber))
            {
                throw new NotSupportedException("Image editing requires a complete current image index and a valid occurrence number.");
            }

            var graph = new PdfGraph(working);
            var page = graph.Pages()[pageNumber - 1];
            PdfValue? replacementReference = null;
            if (replacement is not null)
            {
                var selected = indexed.Images.Single(i => i.ImageNumber == imageNumber);
                var corners = selected.Corners;
                double Distance(int first, int second) => Math.Sqrt(Math.Pow(corners[first].X - corners[second].X, 2) + Math.Pow(corners[first].Y - corners[second].Y, 2));
                var displayWidth = Distance(0, 1);
                var displayHeight = Distance(0, 3);
                if (displayWidth <= 0 || displayHeight <= 0 || !double.IsFinite(displayWidth + displayHeight))
                {
                    throw new NotSupportedException("Image replacement requires a nondegenerate placement.");
                }

                var scaleX = 1d;
                var scaleY = 1d;
                if (fit != PdfImageFit.Stretch)
                {
                    var imageWidth = Number(replacement.Image.Entries!["Width"]);
                    var imageHeight = Number(replacement.Image.Entries["Height"]);
                    var scale = fit == PdfImageFit.Fit ? Math.Min(displayWidth / imageWidth, displayHeight / imageHeight) : Math.Max(displayWidth / imageWidth, displayHeight / imageHeight);
                    scaleX = imageWidth * scale / displayWidth;
                    scaleY = imageHeight * scale / displayHeight;
                }

                if (replacement.Mask is not null)
                {
                    replacement.Image.Entries!["SMask"] = Add(working, replacement.Mask);
                    if (string.CompareOrdinal(working.PdfVersion, "1.4") < 0)
                    {
                        working.PdfVersion = "1.4";
                    }
                }

                var imageReference = Add(working, replacement.Image);
                // A unit Form preserves the original clipping and pending path while cropping Fill.
                var wrapper = Stream(Encoding.ASCII.GetBytes($"q {Syntax(Num(scaleX))} 0 0 {Syntax(Num(scaleY))} {Syntax(Num((1 - scaleX) / 2))} {Syntax(Num((1 - scaleY) / 2))} cm /Image Do Q"));
                wrapper.Entries!["Type"] = Name("XObject");
                wrapper.Entries["Subtype"] = Name("Form");
                wrapper.Entries["BBox"] = Array(Num(0), Num(0), Num(1), Num(1));
                wrapper.Entries["Resources"] = Dict(("XObject", Dict(("Image", imageReference))));
                replacementReference = Add(working, wrapper);
            }

            var localX = 0d;
            var localY = 0d;
            if (deltaX.HasValue)
            {
                var corners = indexed.Images.Single(i => i.ImageNumber == imageNumber).Corners;
                var a = corners[1].X - corners[0].X;
                var b = corners[1].Y - corners[0].Y;
                var c = corners[3].X - corners[0].X;
                var d = corners[3].Y - corners[0].Y;
                var determinant = a * d - b * c;
                localX = (d * deltaX.Value - c * deltaY) / determinant;
                localY = (a * deltaY - b * deltaX.Value) / determinant;
                if (!double.IsFinite(localX) || !double.IsFinite(localY))
                {
                    throw new NotSupportedException("Cannot move a degenerate image placement.");
                }
            }

            var count = 0;
            var removed = false;
            (PdfValue Stream, PdfValue Resources, bool Changed) Rewrite(PdfValue source, PdfValue originalResources, int depth)
            {
                if (depth > 100)
                {
                    throw new InvalidDataException("Form nesting exceeds limit.");
                }

                var resources = CopyDictionary(originalResources);
                var xobjects = CopyDictionary(graph.Get(originalResources, "XObject") ?? Dict());
                var operations = PdfContent.Parse(PdfStreamDecoder.Decode(source));
                var output = new StringBuilder();
                var changed = false;
                foreach (var operation in operations)
                {
                    var op = operation;
                    var moving = false;
                    var target = op.Operator == "Do" ? graph.Get(originalResources, "XObject")?.Entries?.GetValueOrDefault(op.Operands[0].Name!) : null;
                    var subtype = target is null ? null : graph.Get(target, "Subtype")?.Name;
                    if (op.Operator == "BI" || subtype == "Image")
                    {
                        if (++count == imageNumber)
                        {
                            removed = changed = true;
                            if (deltaX.HasValue)
                            {
                                moving = true;
                                output.Append($"q 1 0 0 1 {Syntax(Num(localX))} {Syntax(Num(localY))} cm\n");
                            }
                            else if (replacementReference is not null)
                            {
                                var name = Unique(xobjects, "PdfObjectKitReplacement");
                                xobjects.Entries![name] = replacementReference;
                                op = new("Do", [Name(name)]);
                                output.Append(op.Serialize());
                            }
                            if (!moving)
                            {
                                continue;
                            }
                        }
                    }
                    else if (subtype == "Form")
                    {
                        var form = graph.Resolve(target!);
                        var nested = Rewrite(form, graph.Get(form, "Resources") ?? originalResources, depth + 1);
                        if (nested.Changed)
                        {
                            var name = Unique(xobjects, "PdfObjectKitForm");
                            nested.Stream.Entries!["Resources"] = nested.Resources;
                            xobjects.Entries![name] = Add(working, nested.Stream);
                            op = new("Do", [Name(name)]);
                            changed = true;
                        }
                    }

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
                    if (moving)
                    {
                        output.Append("Q\n");
                    }
                }

                var result = Stream(Encoding.Latin1.GetBytes(output.ToString()));
                result.Entries = new(source.Entries!);
                result.Entries.Remove("Filter");
                result.Entries.Remove("DecodeParms");
                result.Entries.Remove("Length");
                resources.Entries!["XObject"] = xobjects;
                return (result, resources, changed);
            }

            // One resource dictionary for all streams; process as a single sequence.
            var source = Stream(Encoding.Latin1.GetBytes(string.Join("\n", page.Streams.Select(s => Encoding.Latin1.GetString(PdfStreamDecoder.Decode(s.Value))))));
            var result = Rewrite(source, page.Resources, 0);
            if (!removed)
            {
                throw new InvalidDataException("Image occurrence could not be located.");
            }

            page.Dictionary.Entries!["Contents"] = Add(working, result.Stream);
            page.Dictionary.Entries["Resources"] = result.Resources;
        });
    }

    internal static void Edit(PdfObjectDocument document, Action<PdfObjectDocument> action)
    {
        var working = PdfConverter.FromJson(PdfConverter.ToJson(document));
        action(working);
        PdfDocumentValidator.Validate(working);
        document.Objects = working.Objects;
        document.Trailer = working.Trailer;
        document.PdfVersion = working.PdfVersion;
    }

    private static PdfValue Add(PdfObjectDocument document, PdfValue value)
    {
        var id = new PdfReference(checked(document.Objects.Max(o => o.Id.ObjectNumber) + 1));
        document.Objects.Add(new(id, value));
        return Ref(id);
    }

    private static PdfValue CopyDictionary(PdfValue value) => new() { Type = "dictionary", Entries = new(value.Entries!) };

    private static string Unique(PdfValue dictionary, string name)
    {
        while (dictionary.Entries!.ContainsKey(name))
        {
            name += "_";
        }

        return name;
    }
}
