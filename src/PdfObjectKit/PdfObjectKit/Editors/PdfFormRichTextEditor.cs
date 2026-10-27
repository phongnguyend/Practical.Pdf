using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PdfObjectKit.Fonts;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

public static partial class PdfFormEditor
{
    private sealed record RichStyle(bool Bold = false, bool Italic = false, bool Underline = false, double Size = 12, string Color = "000000");

    /// <summary>Creates a rich-text field from the supported XHTML subset: body, p, span, b/strong, i/em, u and br.</summary>
    public static void InsertRichTextField(PdfObjectDocument document, int pageNumber, string fieldName, string xhtml,
        double x, double y, double width, double height, PdfTextAlignment alignment = PdfTextAlignment.Left)
    {
        PdfPageEditor.Edit(document, working =>
        {
            InsertTextField(working, pageNumber, fieldName, "", x, y, width, height, alignment, multiline: true);
            SetRichText(working, fieldName, xhtml, alignment);
        });
    }

    /// <summary>Stores validated XHTML in RV and synchronized plain text in V, and regenerates styled appearances.</summary>
    public static void SetRichText(PdfObjectDocument document, string fieldName, string xhtml, PdfTextAlignment? alignment = null)
    {
        ArgumentNullException.ThrowIfNull(xhtml);
        var runs = new List<(string Text, RichStyle Style)>();
        using var reader = XmlReader.Create(new StringReader(xhtml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 100000 });
        var root = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root ?? throw new ArgumentException("Rich text needs a body element.");
        if (root.Name.LocalName != "body")
        {
            throw new ArgumentException("Rich text needs a body element.");
        }
        void Visit(XElement element, RichStyle style, int depth = 0)
        {
            if (depth > 64)
            {
                throw new NotSupportedException("Rich-text nesting exceeds 64 levels.");
            }
            var tag = element.Name.LocalName;
            if (tag is not ("body" or "p" or "span" or "b" or "strong" or "i" or "em" or "u" or "br")
                || element.Name.NamespaceName is not ("" or "http://www.w3.org/1999/xhtml"))
            {
                throw new NotSupportedException("Unsupported rich-text element: " + tag);
            }
            style = style with { Bold = style.Bold || tag is "b" or "strong", Italic = style.Italic || tag is "i" or "em", Underline = style.Underline || tag == "u" };
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                if (attribute.Name == XNamespace.Xml + "space" && attribute.Value == "preserve")
                {
                    continue;
                }
                if (attribute.Name != "style")
                {
                    throw new NotSupportedException("Unsupported rich-text attribute: " + attribute.Name);
                }
                foreach (var declaration in attribute.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = declaration.Split(':', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2)
                    {
                        throw new ArgumentException("Invalid rich-text style.");
                    }
                    var value = parts[1].ToLowerInvariant();
                    switch (parts[0].ToLowerInvariant())
                    {
                        case "font-weight" when value is "bold" or "normal":
                            style = style with { Bold = value == "bold" };
                            break;
                        case "font-style" when value is "italic" or "normal":
                            style = style with { Italic = value == "italic" };
                            break;
                        case "text-decoration" when value is "underline" or "none":
                            style = style with { Underline = value == "underline" };
                            break;
                        case "font-size" when value.EndsWith("pt") && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size is >= 1 and <= 200:
                            style = style with { Size = size };
                            break;
                        case "color" when value.Length == 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit):
                            style = style with { Color = value[1..] };
                            break;
                        case "font-family" when value is "helvetica" or "sans-serif":
                            break;
                        default:
                            throw new NotSupportedException("Unsupported rich-text style: " + declaration);
                    }
                }
            }
            if (tag == "br")
            {
                if (element.Nodes().Any())
                {
                    throw new ArgumentException("A br element must be empty.");
                }
                runs.Add(("\n", style));
                return;
            }
            foreach (var node in element.Nodes())
            {
                if (node is XElement child)
                {
                    Visit(child, style, depth + 1);
                }
                else if (node is XText text)
                {
                    var value = text.Value.Replace("\r\n", "\n").Replace('\r', '\n');
                    if (value.Any(c => (c < 32 || c > 126) && c != '\n'))
                    {
                        throw new NotSupportedException("Rich-text appearances currently support ASCII text.");
                    }
                    runs.Add((value, style));
                }
            }
            if (tag == "p")
            {
                runs.Add(("\n", style));
            }
        }
        Visit(root, new());
        if (runs.Count > 0 && runs[^1].Text == "\n")
        {
            runs.RemoveAt(runs.Count - 1);
        }
        var plain = string.Concat(runs.Select(r => r.Text));
        foreach (var element in root.DescendantsAndSelf())
        {
            element.Name = XName.Get(element.Name.LocalName, "http://www.w3.org/1999/xhtml");
        }
        PdfPageEditor.Edit(document, working =>
        {
            var form = new PdfFormGraph(working);
            var field = form.Fields.SingleOrDefault(f => f.Model.Name == fieldName) ?? throw new ArgumentException("Field not found.");
            if (field.Model.Kind != PdfFormFieldKind.Text || (field.Model.Flags & ((1 << 13) | (1 << 20) | (1 << 24))) != 0)
            {
                throw new NotSupportedException("Rich text requires a regular text field.");
            }
            var flags = field.Model.Flags | (1 << 12) | (1 << 25);
            field.Node.Entries!["Ff"] = Num(flags & ~(1 << 25));
            SetValue(working, fieldName, plain, alignment);
            form = new PdfFormGraph(working);
            field = form.Fields.Single(f => f.Model.Name == fieldName);
            field.Node.Entries!["Ff"] = Num(flags);
            field.Node.Entries["RV"] = Str(new byte[] { 254, 255 }.Concat(Encoding.BigEndianUnicode.GetBytes(root.ToString(SaveOptions.DisableFormatting))).ToArray());
            var fonts = new[] { "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique" }
                .Select(name => Dict(("Type", Name("Font")), ("Subtype", Name("Type1")), ("BaseFont", Name(name)), ("Encoding", Name("WinAnsiEncoding")))).ToArray();
            var metrics = fonts.Select(font => new PdfNativeFont(form.Graph, font)).ToArray();
            foreach (var widget in field.Widgets)
            {
                var rect = form.Graph.Get(widget, "Rect")!.Items!.Select(Number).ToArray();
                var width = rect[2] - rect[0];
                var height = rect[3] - rect[1];
                var content = new StringBuilder($"q 0 0 {Syntax(Num(width))} {Syntax(Num(height))} re W n /Tx BMC ");
                var line = new List<(char Character, RichStyle Style, double Width, int Font)>();
                var lineWidth = 0d;
                var top = height - 2;
                void Flush()
                {
                    var size = line.Count == 0 ? 12 : line.Max(g => g.Style.Size);
                    var baseline = top - size;
                    var x = field.Model.TextAlignment switch
                    {
                        PdfTextAlignment.Center => (width - lineWidth) / 2,
                        PdfTextAlignment.Right => width - 2 - lineWidth,
                        _ => 2
                    };
                    foreach (var glyph in line)
                    {
                        var rgb = Convert.FromHexString(glyph.Style.Color);
                        var color = string.Join(" ", rgb.Select(c => Syntax(Num(c / 255d))));
                        content.Append($"{color} rg BT /R{glyph.Font} {Syntax(Num(glyph.Style.Size))} Tf 1 0 0 1 {Syntax(Num(x))} {Syntax(Num(baseline))} Tm {Syntax(Str([(byte)glyph.Character]))} Tj ET ");
                        if (glyph.Style.Underline)
                        {
                            content.Append($"{color} RG .5 w {Syntax(Num(x))} {Syntax(Num(baseline - 1))} m {Syntax(Num(x + glyph.Width))} {Syntax(Num(baseline - 1))} l S ");
                        }
                        x += glyph.Width;
                    }
                    top -= size * 1.2;
                    line.Clear();
                    lineWidth = 0;
                }
                foreach (var run in runs)
                {
                    var font = (run.Style.Bold ? 1 : 0) + (run.Style.Italic ? 2 : 0);
                    foreach (var character in run.Text)
                    {
                        if (character == '\n')
                        {
                            Flush();
                            continue;
                        }
                        var advance = metrics[font].Decode([(byte)character]).Single().Width * run.Style.Size;
                        if (line.Count > 0 && lineWidth + advance > width - 4)
                        {
                            Flush();
                        }
                        line.Add((character, run.Style, advance, font));
                        lineWidth += advance;
                    }
                }
                Flush();
                content.Append("EMC Q");
                var appearance = Stream(Encoding.ASCII.GetBytes(content.ToString()));
                appearance.Entries!["Type"] = Name("XObject");
                appearance.Entries["Subtype"] = Name("Form");
                appearance.Entries["BBox"] = Array(Num(0), Num(0), Num(width), Num(height));
                appearance.Entries["Resources"] = Dict(("Font", new PdfValue { Type = "dictionary", Entries = fonts.Select((font, index) => (font, index)).ToDictionary(item => "R" + item.index, item => item.font) }));
                var id = new PdfReference(checked(working.Objects.Max(o => o.Id.ObjectNumber) + 1));
                working.Objects.Add(new(id, appearance));
                widget.Entries!["AP"] = Dict(("N", Ref(id)));
            }
        });
    }
}
