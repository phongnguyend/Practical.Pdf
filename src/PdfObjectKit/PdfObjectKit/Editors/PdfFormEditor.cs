using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Graphs;
using PdfObjectKit.Fonts;
using PdfObjectKit.Models;
using PdfObjectKit.Validators;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

/// <summary>Fills existing AcroForm fields atomically. Does not flatten fields or execute scripts.</summary>
public static partial class PdfFormEditor
{
    /// <summary>
    /// Sets text or a dropdown export value, or a checkbox/radio appearance-state name ("Off" clears).
    /// Text appearances are regenerated as plain black Helvetica, 12pt, clipped to the widget rectangle.
    /// Existing custom appearance styling is replaced. ASCII single-line text and dropdowns are supported.
    /// </summary>
    public static void SetValue(PdfObjectDocument document, string fieldName, string value, PdfTextAlignment? textAlignment = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentNullException.ThrowIfNull(value);
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

        switch (field.Model.Kind)
        {
            case PdfFormFieldKind.CheckBox:
            case PdfFormFieldKind.RadioButton:
                SetButton(form, field, value);
                break;
            case PdfFormFieldKind.Text:
            case PdfFormFieldKind.Choice:
                SetText(working, form, field, value, textAlignment ?? field.Model.TextAlignment);
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

    private static void SetText(PdfObjectDocument document, PdfFormGraph form, PdfFormGraph.Field field, string value, PdfTextAlignment textAlignment)
    {
        var flags = field.Model.Flags;
        var display = value;
        if (field.Model.Kind == PdfFormFieldKind.Text)
        {
            if ((flags & ((1 << 12) | (1 << 13) | (1 << 20) | (1 << 24) | (1 << 25))) != 0)
            {
                throw new NotSupportedException("Multiline, password, file-select, comb, and rich-text fields are not supported.");
            }

            if (field.Inherited.TryGetValue("MaxLen", out var limit) && value.Length > Number(limit))
            {
                throw new ArgumentException("Value exceeds MaxLen.", nameof(value));
            }
        }
        else
        {
            if ((flags & (1 << 17)) == 0 || (flags & (1 << 21)) != 0)
            {
                throw new NotSupportedException("Only single-selection dropdowns are supported.");
            }

            var choice = field.Model.Choices.FirstOrDefault(c => c.ExportValue == value);
            if (choice is null && (flags & (1 << 18)) == 0 && value.Length != 0)
            {
                throw new ArgumentException("Value is not a dropdown export value.", nameof(value));
            }

            display = choice?.DisplayValue ?? value;
            field.Node.Entries!.Remove("I");
        }

        if (display.Any(c => c < 32 || c > 126))
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
        var textWidth = new PdfNativeFont(form.Graph, fontDictionary).Decode(Encoding.ASCII.GetBytes(display)).Sum(g => g.Width) * 12;
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
            appearance.Entries!["Type"] = Name("XObject");
            appearance.Entries["Subtype"] = Name("Form");
            appearance.Entries["BBox"] = Array(Num(0), Num(0), Num(rect[2] - rect[0]), Num(rect[3] - rect[1]));
            appearance.Entries["Resources"] = Dict(("Font", Dict((fontName, font))));
            widget.Entries!["AP"] = Dict(("N", Add(appearance)));
            widget.Entries["DA"] = field.Node.Entries["DA"];
            widget.Entries["Q"] = Num((int)textAlignment);
        }

        field.Node.Entries!["V"] = Str(new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(value)).ToArray());
    }
}
