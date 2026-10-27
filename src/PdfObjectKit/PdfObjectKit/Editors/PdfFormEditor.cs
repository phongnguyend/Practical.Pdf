using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Graphs;
using PdfObjectKit.Fonts;
using PdfObjectKit.Models;
using PdfObjectKit.Validators;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

/// <summary>Creates and fills AcroForm fields atomically. Does not flatten fields or execute scripts.</summary>
public static partial class PdfFormEditor
{
    /// <summary>
    /// Sets text or a dropdown export value, or a checkbox/radio appearance-state name ("Off" clears).
    /// Text appearances are regenerated as plain black Helvetica, 12pt, clipped to the widget rectangle.
    /// Existing custom appearance styling is replaced. ASCII text (including multiline), dropdowns and single-selection list boxes are supported.
    /// </summary>
    public static void SetValue(PdfObjectDocument document, string fieldName, string value, PdfTextAlignment? textAlignment = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        SetFieldValue(document, fieldName, value, textAlignment, null);
    }

    /// <summary>Sets a multi-select list box using export values. An empty collection clears selection. Values are stored in option order.</summary>
    public static void SetValues(PdfObjectDocument document, string fieldName, IEnumerable<string> values, PdfTextAlignment? textAlignment = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var selections = values.ToList();
        if (selections.Any(v => v is null) || selections.Distinct(StringComparer.Ordinal).Count() != selections.Count)
        {
            throw new ArgumentException("Selection values must be non-null and unique.", nameof(values));
        }
        SetFieldValue(document, fieldName, "", textAlignment, selections);
    }

    private static void SetFieldValue(PdfObjectDocument document, string fieldName, string value, PdfTextAlignment? textAlignment, List<string>? values)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        if (textAlignment.HasValue && !Enum.IsDefined(textAlignment.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(textAlignment));
        }

        PdfDocumentValidator.Validate(document);
        var working = PdfConverter.FromJson(PdfConverter.ToJson(document));
        var form = new PdfFormGraph(working);
        var matches = form.Fields.Where(f => f.Model.Name == fieldName).ToList();
        if (matches.Count != 1)
        {
            throw new ArgumentException("Field name must identify exactly one existing field.", nameof(fieldName));
        }

        var field = matches[0];
        if (field.Model.IsReadOnly)
        {
            throw new InvalidOperationException("The field is read-only.");
        }

        if (form.Fields.Any(f => f.Model.Kind == PdfFormFieldKind.Signature && f.Inherited.ContainsKey("V"))
            || form.Graph.Get(form.AcroForm!, "CO")?.Items?.Count > 0)
        {
            throw new NotSupportedException("Editing signed forms or forms with calculation order is not supported.");
        }

        if (form.Graph.Get(field.Node, "AA") is not null || field.Widgets.Any(w => form.Graph.Get(w, "AA") is not null))
        {
            throw new NotSupportedException("Fields with additional actions are not supported.");
        }

        if (textAlignment.HasValue && field.Model.Kind is not (PdfFormFieldKind.Text or PdfFormFieldKind.Choice))
        {
            throw new ArgumentException("Text alignment requires a text or choice field.", nameof(textAlignment));
        }

        var multiSelect = field.Model.Kind == PdfFormFieldKind.Choice && (field.Model.Flags & (1 << 21)) != 0;
        if (values is not null && (!multiSelect || (field.Model.Flags & (1 << 17)) != 0))
        {
            throw new ArgumentException("SetValues requires a multi-select list box.", nameof(fieldName));
        }
        if (multiSelect && values is null)
        {
            throw new ArgumentException("Use SetValues for a multi-select list box.", nameof(fieldName));
        }

        switch (field.Model.Kind)
        {
            case PdfFormFieldKind.CheckBox:
            case PdfFormFieldKind.RadioButton:
                SetButton(form, field, value);
                break;
            case PdfFormFieldKind.Text:
            case PdfFormFieldKind.Choice:
                SetText(working, form, field, value, textAlignment ?? field.Model.TextAlignment, values);
                break;
            default:
                throw new NotSupportedException("Editing this field type is not supported.");
        }

        PdfDocumentValidator.Validate(working);
        document.Objects = working.Objects;
        document.Trailer = working.Trailer;
        document.PdfVersion = working.PdfVersion;
    }

    public static void SetChecked(PdfObjectDocument document, string fieldName, bool isChecked)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        PdfDocumentValidator.Validate(document);
        var field = new PdfFormGraph(document).Fields.SingleOrDefault(f => f.Model.Name == fieldName)
            ?? throw new ArgumentException("Field not found.", nameof(fieldName));
        if (field.Model.Kind != PdfFormFieldKind.CheckBox)
        {
            throw new ArgumentException("SetChecked requires a checkbox.", nameof(fieldName));
        }

        var states = field.Model.Widgets.SelectMany(w => w.AppearanceStates).Where(s => s != "Off").Distinct().ToList();
        if (states.Count != 1)
        {
            throw new NotSupportedException("Checkbox must have exactly one non-Off appearance state.");
        }

        SetValue(document, fieldName, isChecked ? states[0] : "Off");
    }

    private static void SetButton(PdfFormGraph form, PdfFormGraph.Field field, string value)
    {
        if (field.Widgets.Count == 0)
        {
            throw new NotSupportedException("Button widgets are required.");
        }

        var flags = field.Model.Flags;
        if (field.Model.Kind == PdfFormFieldKind.RadioButton && value == "Off" && (flags & (1 << 14)) != 0)
        {
            throw new InvalidOperationException("Radio group does not permit clearing the selection.");
        }

        var appearances = field.Widgets.Select(w => form.Graph.Get(w, "AP") is { } ap ? form.Graph.Get(ap, "N") : null).ToList();
        if (appearances.Any(a => a?.Type != "dictionary" || !a.Entries!.ContainsKey("Off")))
        {
            throw new NotSupportedException("Buttons require existing normal appearances including Off.");
        }

        var matching = appearances.Count(a => a!.Entries!.ContainsKey(value));
        if (value != "Off" && (matching == 0 || (field.Model.Kind == PdfFormFieldKind.RadioButton && matching > 1 && (flags & (1 << 25)) == 0)))
        {
            throw new ArgumentException("Unknown or ambiguous button export state.", nameof(value));
        }

        for (var i = 0; i < field.Widgets.Count; i++)
        {
            var state = appearances[i]!.Entries!.ContainsKey(value) ? value : "Off";
            if (form.Graph.Resolve(appearances[i]!.Entries![state]).Type != "stream")
            {
                throw new InvalidDataException("Button appearance must be a stream.");
            }

            field.Widgets[i].Entries!["AS"] = Name(state);
        }

        field.Node.Entries!["V"] = Name(value);
    }

    private static void SetText(PdfObjectDocument document, PdfFormGraph form, PdfFormGraph.Field field, string value, PdfTextAlignment textAlignment, List<string>? values)
    {
        var flags = field.Model.Flags;
        var display = value;
        var multiline = field.Model.Kind == PdfFormFieldKind.Text && (flags & (1 << 12)) != 0;
        var listBox = field.Model.Kind == PdfFormFieldKind.Choice && (flags & (1 << 17)) == 0;
        var selectedIndex = -1;
        var selectedIndices = new HashSet<int>();
        if (field.Model.Kind == PdfFormFieldKind.Text)
        {
            if ((flags & ((1 << 13) | (1 << 20) | (1 << 24) | (1 << 25))) != 0)
            {
                throw new NotSupportedException("Password, file-select, comb, and rich-text fields are not supported.");
            }

            if (field.Inherited.TryGetValue("MaxLen", out var limit) && value.Length > Number(limit))
            {
                throw new ArgumentException("Value exceeds MaxLen.", nameof(value));
            }
        }
        else
        {
            var choice = field.Model.Choices.FirstOrDefault(c => c.ExportValue == value);
            if (choice is null && (listBox || (flags & (1 << 18)) == 0) && value.Length != 0)
            {
                throw new ArgumentException("Value is not a choice export value.", nameof(value));
            }

            display = choice?.DisplayValue ?? value;
            field.Node.Entries!.Remove("I");
            selectedIndex = field.Model.Choices.FindIndex(c => c.ExportValue == value);
            if (values is not null)
            {
                foreach (var selection in values)
                {
                    var indices = field.Model.Choices.Select((choice, index) => (choice, index))
                        .Where(item => item.choice.ExportValue == selection).Select(item => item.index).ToList();
                    if (indices.Count != 1)
                    {
                        throw new ArgumentException("Each selection must identify exactly one choice export value.", nameof(values));
                    }
                    selectedIndices.Add(indices[0]);
                }
                selectedIndex = selectedIndices.Count == 0 ? -1 : selectedIndices.Min();
            }
            else if (selectedIndex >= 0)
            {
                selectedIndices.Add(selectedIndex);
            }
            if (listBox)
            {
                field.Node.Entries["I"] = Array(selectedIndices.Order().Select(index => Num(index)).ToArray());
                field.Node.Entries["TI"] = Num(Math.Max(0, selectedIndex));
                if (field.Model.Choices.Any(c => c.DisplayValue.Any(ch => ch < 32 || ch > 126)))
                {
                    throw new NotSupportedException("List labels must be printable ASCII.");
                }
            }
        }

        if (display.Any(c => (c < 32 || c > 126) && !(multiline && c is '\r' or '\n')))
        {
            throw new NotSupportedException("Appearance generation currently supports printable ASCII only.");
        }

        PdfValue Add(PdfValue item)
        {
            var id = new PdfReference(checked(document.Objects.Max(o => o.Id.ObjectNumber) + 1));
            document.Objects.Add(new(id, item));
            return Ref(id);
        }

        var fontDictionary = Dict(("Type", Name("Font")), ("Subtype", Name("Type1")), ("BaseFont", Name("Helvetica")), ("Encoding", Name("WinAnsiEncoding")));
        var nativeFont = new PdfNativeFont(form.Graph, fontDictionary);
        double Measure(string text) => nativeFont.Decode(Encoding.ASCII.GetBytes(text)).Sum(g => g.Width) * 12;
        var textWidth = multiline ? 0 : Measure(display);
        var font = Add(fontDictionary);
        var dr = form.Graph.Get(form.AcroForm!, "DR");
        var resources = dr?.Entries is { } original ? new Dictionary<string, PdfValue>(original) : new();
        var originalFonts = dr is null ? null : form.Graph.Get(dr, "Font");
        var fonts = originalFonts?.Entries is { } fontEntries ? new Dictionary<string, PdfValue>(fontEntries) : new();
        var fontName = "PdfObjectKitFormFont";
        while (fonts.ContainsKey(fontName))
        {
            fontName += "_";
        }

        fonts[fontName] = font;
        resources["Font"] = new PdfValue { Type = "dictionary", Entries = fonts };
        form.AcroForm!.Entries!["DR"] = new PdfValue { Type = "dictionary", Entries = resources };
        field.Node.Entries!["DA"] = Str(Encoding.ASCII.GetBytes($"/{fontName} 12 Tf 0 g"));
        field.Node.Entries["Q"] = Num((int)textAlignment);
        foreach (var widget in field.Widgets)
        {
            var rect = form.Graph.Get(widget, "Rect")?.Items?.Select(v => Number(form.Graph.Resolve(v))).ToArray();
            if (rect is not { Length: 4 } || rect[2] <= rect[0] || rect[3] <= rect[1])
            {
                throw new InvalidDataException("Widget requires a positive rectangle.");
            }

            if (form.Graph.Get(widget, "MK") is { } mk && form.Graph.Number(mk, "R") != 0)
            {
                throw new NotSupportedException("Rotated widget appearances are not supported.");
            }

            var width = Syntax(Num(rect[2] - rect[0]));
            var height = Syntax(Num(rect[3] - rect[1]));
            var x = Syntax(Num(textAlignment switch
            {
                PdfTextAlignment.Center => (rect[2] - rect[0] - textWidth) / 2,
                PdfTextAlignment.Right => rect[2] - rect[0] - 2 - textWidth,
                _ => 2
            }));
            var y = Syntax(Num(Math.Max(2, (rect[3] - rect[1] - 12) / 2 + 2)));
            var appearance = Stream(Encoding.ASCII.GetBytes($"q 0 0 {width} {height} re W n /Tx BMC BT /{fontName} 12 Tf 0 g 1 0 0 1 {x} {y} Tm {Syntax(Str(Encoding.ASCII.GetBytes(display)))} Tj ET EMC Q"));
            if (multiline || listBox)
            {
                var content = new StringBuilder($"q 0 0 {width} {height} re W n /Tx BMC ");
                var lines = listBox
                    ? field.Model.Choices.Skip(Math.Max(0, selectedIndex)).Select(c => c.DisplayValue).ToList()
                    : WrapFormText(display, Math.Max(0, rect[2] - rect[0] - 4), Measure);
                for (var i = 0; i < lines.Count; i++)
                {
                    var baseline = rect[3] - rect[1] - 14 - i * 14;
                    if (baseline < 0)
                    {
                        break;
                    }
                    if (listBox && selectedIndices.Contains(Math.Max(0, selectedIndex) + i))
                    {
                        content.Append($"0.2 0.4 0.8 rg 0 {Syntax(Num(baseline - 2))} {width} 14 re f ");
                    }
                    var lineX = textAlignment switch
                    {
                        PdfTextAlignment.Center => (rect[2] - rect[0] - Measure(lines[i])) / 2,
                        PdfTextAlignment.Right => rect[2] - rect[0] - 2 - Measure(lines[i]),
                        _ => 2
                    };
                    var color = listBox && selectedIndices.Contains(Math.Max(0, selectedIndex) + i) ? "1 g" : "0 g";
                    content.Append($"BT /{fontName} 12 Tf {color} 1 0 0 1 {Syntax(Num(lineX))} {Syntax(Num(baseline))} Tm {Syntax(Str(Encoding.ASCII.GetBytes(lines[i])))} Tj ET ");
                }
                content.Append("EMC Q");
                appearance = Stream(Encoding.ASCII.GetBytes(content.ToString()));
            }
            appearance.Entries!["Type"] = Name("XObject");
            appearance.Entries["Subtype"] = Name("Form");
            appearance.Entries["BBox"] = Array(Num(0), Num(0), Num(rect[2] - rect[0]), Num(rect[3] - rect[1]));
            appearance.Entries["Resources"] = Dict(("Font", Dict((fontName, font))));
            widget.Entries!["AP"] = Dict(("N", Add(appearance)));
            widget.Entries["DA"] = field.Node.Entries["DA"];
            widget.Entries["Q"] = Num((int)textAlignment);
        }

        PdfValue TextValue(string text) => Str(new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray());
        field.Node.Entries!["V"] = values is null ? TextValue(value)
            : Array(selectedIndices.Order().Select(index => TextValue(field.Model.Choices[index].ExportValue)).ToArray());
    }

    private static List<string> WrapFormText(string text, double width, Func<string, double> measure)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var remaining = paragraph;
            while (remaining.Length > 0)
            {
                var count = 0;
                var measuredWidth = 0d;
                while (count < remaining.Length)
                {
                    var nextWidth = measure(remaining[count].ToString());
                    if (count > 0 && measuredWidth + nextWidth > width)
                    {
                        break;
                    }
                    measuredWidth += nextWidth;
                    count++;
                }
                var space = count < remaining.Length ? remaining.LastIndexOf(' ', count - 1, count) : -1;
                if (space > 0)
                {
                    lines.Add(remaining[..space]);
                    remaining = remaining[(space + 1)..];
                }
                else
                {
                    lines.Add(remaining[..count]);
                    remaining = remaining[count..];
                }
            }
            if (paragraph.Length == 0)
            {
                lines.Add("");
            }
        }
        return lines;
    }

    /// <summary>Creates a single-selection list box. Empty value clears the selection.</summary>
    public static void InsertListBox(PdfObjectDocument document, int pageNumber, string fieldName, IEnumerable<PdfFormChoice> choices,
        string value, double x, double y, double width, double height, PdfTextAlignment textAlignment = PdfTextAlignment.Left)
    {
        ArgumentNullException.ThrowIfNull(choices);
        InsertChoiceOrButton(document, pageNumber, fieldName, value, x, y, width, height,
            PdfFormFieldKind.Choice, choices.ToList(), textAlignment, listBox: true);
    }

    /// <summary>Creates a list box allowing multiple selections. Values are export values; an empty collection selects nothing.</summary>
    public static void InsertMultiSelectListBox(PdfObjectDocument document, int pageNumber, string fieldName, IEnumerable<PdfFormChoice> choices,
        IEnumerable<string> values, double x, double y, double width, double height, PdfTextAlignment textAlignment = PdfTextAlignment.Left)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(values);
        InsertChoiceOrButton(document, pageNumber, fieldName, "", x, y, width, height,
            PdfFormFieldKind.Choice, choices.ToList(), textAlignment, listBox: true, values: values.ToList());
    }

    /// <summary>Creates a pushbutton with a printable ASCII caption. No action is attached.</summary>
    public static void InsertPushButton(PdfObjectDocument document, int pageNumber, string fieldName, string caption,
        double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(caption);
        InsertAppearanceField(document, pageNumber, fieldName, caption, x, y, width, height, PdfFormFieldKind.PushButton);
    }

    /// <summary>Creates an unsigned signature field. Does not create a signature value or sign the document.</summary>
    public static void InsertSignatureField(PdfObjectDocument document, int pageNumber, string fieldName,
        double x, double y, double width, double height)
    {
        InsertAppearanceField(document, pageNumber, fieldName, "Unsigned signature", x, y, width, height, PdfFormFieldKind.Signature);
    }

    /// <summary>Replaces a pushbutton caption and its normal appearance. Custom appearance styling is replaced.</summary>
    public static void SetPushButtonCaption(PdfObjectDocument document, string fieldName, string caption)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentNullException.ThrowIfNull(caption);
        PdfPageEditor.Edit(document, working =>
        {
            var form = new PdfFormGraph(working);
            var matches = form.Fields.Where(f => f.Model.Name == fieldName).ToList();
            if (matches.Count != 1 || matches[0].Model.Kind != PdfFormFieldKind.PushButton)
            {
                throw new ArgumentException("Choose exactly one pushbutton field.", nameof(fieldName));
            }
            var field = matches[0];
            EnsureAppearanceFieldEditable(form);
            if (field.Model.IsReadOnly || form.Graph.Get(field.Node, "AA") is not null
                || field.Widgets.Any(w => form.Graph.Get(w, "AA") is not null))
            {
                throw new NotSupportedException("Read-only fields and fields with additional actions cannot be edited.");
            }
            if (field.Widgets.Count == 0)
            {
                throw new NotSupportedException("Pushbutton widgets are required.");
            }
            foreach (var widget in field.Widgets)
            {
                var rect = form.Graph.Get(widget, "Rect")?.Items?.Select(v => Number(form.Graph.Resolve(v))).ToArray();
                if (rect is not { Length: 4 } || rect.Any(v => !double.IsFinite(v)) || rect[2] <= rect[0] || rect[3] <= rect[1])
                {
                    throw new InvalidDataException("Widget requires a positive rectangle.");
                }
                if (form.Graph.Get(widget, "MK") is { } mk && form.Graph.Number(mk, "R") != 0)
                {
                    throw new NotSupportedException("Rotated widget appearances are not supported.");
                }
                SetSimpleFieldAppearance(working, form, widget, caption, rect[2] - rect[0], rect[3] - rect[1], true);
            }
        });
    }

    private static void EnsureAppearanceFieldEditable(PdfFormGraph form)
    {
        if (form.Fields.Any(f => f.Model.Kind == PdfFormFieldKind.Signature && f.Inherited.ContainsKey("V"))
            || form.AcroForm is not null && form.Graph.Get(form.AcroForm, "CO")?.Items?.Count > 0)
        {
            throw new NotSupportedException("Editing signed forms or forms with calculation order is not supported.");
        }
    }

    private static void InsertAppearanceField(PdfObjectDocument document, int pageNumber, string fieldName, string caption,
        double x, double y, double width, double height, PdfFormFieldKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        if (fieldName.Contains('.') || fieldName.Any(char.IsControl)
            || !new[] { x, y, width, height, x + width, y + height }.All(double.IsFinite) || width <= 0 || height <= 0)
        {
            throw new ArgumentException("Use a top-level name and finite positive dimensions.");
        }
        PdfPageEditor.Edit(document, working =>
        {
            var form = new PdfFormGraph(working);
            EnsureAppearanceFieldEditable(form);
            var graph = form.Graph;
            var pages = graph.Pages();
            if (pageNumber < 1 || pageNumber > pages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pageNumber));
            }
            if (form.Fields.Any(f => f.Model.Name == fieldName || f.Model.Name.StartsWith(fieldName + ".", StringComparison.Ordinal)))
            {
                throw new ArgumentException("The field name already exists.", nameof(fieldName));
            }
            var acro = form.AcroForm ?? Dict(("Fields", Array()));
            graph.Resolve(working.Trailer.Entries!["Root"]).Entries!["AcroForm"] = acro;
            var pushButton = kind == PdfFormFieldKind.PushButton;
            var widget = Dict(("Type", Name("Annot")), ("Subtype", Name("Widget")), ("FT", Name(pushButton ? "Btn" : "Sig")),
                ("Ff", Num(pushButton ? 1 << 16 : 0)), ("F", Num(4)),
                ("T", Str(new byte[] { 0xfe, 0xff }.Concat(Encoding.BigEndianUnicode.GetBytes(fieldName)).ToArray())),
                ("Rect", Array(Num(x), Num(y), Num(x + width), Num(y + height))));
            SetSimpleFieldAppearance(working, form, widget, caption, width, height, pushButton);
            var id = new PdfReference(checked(working.Objects.Max(o => o.Id.ObjectNumber) + 1));
            working.Objects.Add(new(id, widget));
            acro.Entries!["Fields"] = new() { Type = "array", Items = [.. graph.Get(acro, "Fields")?.Items ?? [], Ref(id)] };
            var page = pages[pageNumber - 1];
            page.Dictionary.Entries!["Annots"] = new() { Type = "array", Items = [.. graph.Get(page.Dictionary, "Annots")?.Items ?? [], Ref(id)] };
            if (!pushButton)
            {
                acro.Entries["SigFlags"] = Num(checked((int)graph.Number(acro, "SigFlags")) | 1);
            }
        });
    }

    private static void SetSimpleFieldAppearance(PdfObjectDocument document, PdfFormGraph form, PdfValue widget,
        string caption, double width, double height, bool pushButton)
    {
        if (caption.Any(c => c < 32 || c > 126))
        {
            throw new ArgumentException("Captions must contain printable ASCII only.", nameof(caption));
        }
        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(checked(document.Objects.Max(o => o.Id.ObjectNumber) + 1));
            document.Objects.Add(new(id, value));
            return Ref(id);
        }
        var font = Dict(("Type", Name("Font")), ("Subtype", Name("Type1")), ("BaseFont", Name("Helvetica")), ("Encoding", Name("WinAnsiEncoding")));
        var textWidth = new PdfNativeFont(form.Graph, font).Decode(Encoding.ASCII.GetBytes(caption)).Sum(g => g.Width) * 12;
        var w = Syntax(Num(width));
        var h = Syntax(Num(height));
        var content = $"q 0 0 {w} {h} re W n {(pushButton ? ".94" : "1")} g 0 0 {w} {h} re f .5 G 1 w .5 .5 {Syntax(Num(Math.Max(0, width - 1)))} {Syntax(Num(Math.Max(0, height - 1)))} re S "
            + $"BT /F1 12 Tf 0 g 1 0 0 1 {Syntax(Num((width - textWidth) / 2))} {Syntax(Num(Math.Max(2, (height - 12) / 2 + 2)))} Tm {Syntax(Str(Encoding.ASCII.GetBytes(caption)))} Tj ET Q";
        var appearance = Stream(Encoding.ASCII.GetBytes(content));
        appearance.Entries!["Type"] = Name("XObject");
        appearance.Entries["Subtype"] = Name("Form");
        appearance.Entries["BBox"] = Array(Num(0), Num(0), Num(width), Num(height));
        appearance.Entries["Resources"] = Dict(("Font", Dict(("F1", Add(font)))));
        widget.Entries!["AP"] = Dict(("N", Add(appearance)));
        widget.Entries.Remove("AS");
        if (pushButton)
        {
            // Caption-only appearance: discard old icon/rollover caption settings; rotated widgets are rejected before editing.
            var characteristics = Dict(("CA", Str(Encoding.ASCII.GetBytes(caption))), ("BG", Array(Num(.94))), ("BC", Array(Num(.5))));
            widget.Entries["MK"] = characteristics;
        }
    }

    public static void InsertCheckBox(PdfObjectDocument document, int pageNumber, string fieldName, bool isChecked,
        double x, double y, double width, double height, string exportValue = "Yes")
    {
        ValidateState(exportValue);
        InsertChoiceOrButton(document, pageNumber, fieldName, isChecked ? exportValue : "Off", x, y, width, height,
            PdfFormFieldKind.CheckBox, [new(exportValue, "")]);
    }

    /// <summary>Creates a noneditable, single-selection dropdown with explicit export/display values.</summary>
    public static void InsertDropdown(PdfObjectDocument document, int pageNumber, string fieldName, IEnumerable<PdfFormChoice> choices,
        string value, double x, double y, double width, double height, PdfTextAlignment textAlignment = PdfTextAlignment.Left)
    {
        ArgumentNullException.ThrowIfNull(choices);
        InsertChoiceOrButton(document, pageNumber, fieldName, value, x, y, width, height,
            PdfFormFieldKind.Choice, choices.ToList(), textAlignment);
    }

    /// <summary>Creates a vertical radio group. Each option occupies an equal-height row inside the supplied rectangle. Off clears selection.</summary>
    public static void InsertRadioGroup(PdfObjectDocument document, int pageNumber, string fieldName, IEnumerable<PdfFormChoice> choices,
        string value, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var options = choices.ToList();
        foreach (var option in options)
        {
            ArgumentNullException.ThrowIfNull(option);
            ValidateState(option.ExportValue);
        }
        InsertChoiceOrButton(document, pageNumber, fieldName, value, x, y, width, height, PdfFormFieldKind.RadioButton, options);
    }

    private static void ValidateState(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "Off" || value.Any(c => c < 33 || c > 126))
        {
            throw new ArgumentException("Button export states must be printable ASCII without spaces; Off is reserved.");
        }
    }

    private static void InsertChoiceOrButton(PdfObjectDocument document, int pageNumber, string fieldName, string value,
        double x, double y, double width, double height, PdfFormFieldKind kind, List<PdfFormChoice> choices,
        PdfTextAlignment alignment = PdfTextAlignment.Left, bool listBox = false, List<string>? values = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentNullException.ThrowIfNull(value);
        if (fieldName.Contains('.') || fieldName.Any(char.IsControl) || !new[] { x, y, width, height, x + width, y + height }.All(double.IsFinite)
            || width <= 0 || height <= 0 || choices.Count is < 1 or > 100
            || choices.Any(c => c is null || string.IsNullOrWhiteSpace(c.ExportValue) || c.DisplayValue is null || c.DisplayValue.Any(ch => ch < 32 || ch > 126))
            || choices.Select(c => c.ExportValue).Distinct(StringComparer.Ordinal).Count() != choices.Count)
        {
            throw new ArgumentException("Use a valid top-level name, positive dimensions, and 1â€“100 unique choices with printable ASCII labels.");
        }
        PdfPageEditor.Edit(document, working =>
        {
            var form = new PdfFormGraph(working);
            var graph = form.Graph;
            var pages = graph.Pages();
            if (pageNumber < 1 || pageNumber > pages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pageNumber));
            }
            if (form.Fields.Any(f => f.Model.Name == fieldName || f.Model.Name.StartsWith(fieldName + ".", StringComparison.Ordinal)))
            {
                throw new ArgumentException("The field name already exists.");
            }
            PdfValue Add(PdfValue item)
            {
                var id = new PdfReference(checked(working.Objects.Max(o => o.Id.ObjectNumber) + 1));
                working.Objects.Add(new(id, item));
                return Ref(id);
            }
            PdfValue Text(string text) => Str(new byte[] { 0xfe, 0xff }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray());
            var page = pages[pageNumber - 1];
            var acro = form.AcroForm ?? Dict(("Fields", Array()));
            graph.Resolve(working.Trailer.Entries!["Root"]).Entries!["AcroForm"] = acro;
            var field = Dict(("FT", Name(kind == PdfFormFieldKind.Choice ? "Ch" : "Btn")), ("T", Text(fieldName)),
                ("Ff", Num(kind == PdfFormFieldKind.Choice ? (listBox ? (values is null ? 0 : 1 << 21) : 1 << 17) : kind == PdfFormFieldKind.RadioButton ? 1 << 15 : 0)));
            var fieldRef = Add(field);
            if (kind == PdfFormFieldKind.Choice)
            {
                field.Entries!["Opt"] = Array(choices.Select(c => Array(Text(c.ExportValue), Text(c.DisplayValue))).ToArray());
            }
            var rows = kind == PdfFormFieldKind.RadioButton ? choices.Count : 1;
            var rowHeight = height / rows;
            if (kind == PdfFormFieldKind.RadioButton && (rowHeight < 16 || width < 24))
            {
                throw new ArgumentException("Radio groups need at least 16 points of height per option and 24 points of width.");
            }
            var font = kind == PdfFormFieldKind.RadioButton ? Add(Dict(("Type", Name("Font")), ("Subtype", Name("Type1")), ("BaseFont", Name("Helvetica")), ("Encoding", Name("WinAnsiEncoding")))) : null;
            var widgets = new List<PdfValue>();
            for (var i = 0; i < rows; i++)
            {
                var bottom = y + height - (i + 1) * rowHeight;
                var widget = Dict(("Type", Name("Annot")), ("Subtype", Name("Widget")), ("Parent", fieldRef), ("F", Num(4)),
                    ("Rect", Array(Num(x), Num(bottom), Num(x + width), Num(bottom + rowHeight))));
                if (kind != PdfFormFieldKind.Choice)
                {
                    PdfValue Appearance(bool selected)
                    {
                        var side = Math.Min(16, Math.Min(width, rowHeight));
                        var circle = ".9 .5 m .9 .7209 .7209 .9 .5 .9 c .2791 .9 .1 .7209 .1 .5 c .1 .2791 .2791 .1 .5 .1 c .7209 .1 .9 .2791 .9 .5 c h";
                        var mark = kind == PdfFormFieldKind.RadioButton
                            ? circle + " S " + (selected ? "q .5 0 0 .5 .25 .25 cm " + circle + " f Q " : "")
                            : ".1 .1 .8 .8 re S " + (selected ? ".22 .5 m .42 .28 l .8 .76 l S " : "");
                        var content = $"q 0 g 0 G {Syntax(Num(side))} 0 0 {Syntax(Num(side))} 0 {Syntax(Num((rowHeight - side) / 2))} cm .07 w {mark} Q";
                        if (kind == PdfFormFieldKind.RadioButton)
                        {
                            content += $" q 0 0 {Syntax(Num(width))} {Syntax(Num(rowHeight))} re W n BT /F1 12 Tf 0 g 1 0 0 1 20 {Syntax(Num((rowHeight - 12) / 2 + 2))} Tm {Syntax(Str(Encoding.ASCII.GetBytes(choices[i].DisplayValue)))} Tj ET Q";
                        }
                        var appearance = Stream(Encoding.ASCII.GetBytes(content));
                        appearance.Entries!["Type"] = Name("XObject");
                        appearance.Entries["Subtype"] = Name("Form");
                        appearance.Entries["BBox"] = Array(Num(0), Num(0), Num(width), Num(rowHeight));
                        appearance.Entries["Resources"] = font is null ? Dict() : Dict(("Font", Dict(("F1", font))));
                        return Add(appearance);
                    }
                    widget.Entries!["AP"] = Dict(("N", Dict(("Off", Appearance(false)), (choices[i].ExportValue, Appearance(true)))));
                    widget.Entries["AS"] = Name("Off");
                }
                var widgetRef = Add(widget);
                widgets.Add(widgetRef);
            }
            field.Entries!["Kids"] = Array(widgets.ToArray());
            acro.Entries!["Fields"] = new() { Type = "array", Items = [.. graph.Get(acro, "Fields")?.Items ?? [], fieldRef] };
            page.Dictionary.Entries!["Annots"] = new() { Type = "array", Items = [.. graph.Get(page.Dictionary, "Annots")?.Items ?? [], .. widgets] };
            if (values is not null)
            {
                SetValues(working, fieldName, values, alignment);
            }
            else
            {
                SetValue(working, fieldName, value, kind == PdfFormFieldKind.Choice ? alignment : null);
            }
        });
    }
}
