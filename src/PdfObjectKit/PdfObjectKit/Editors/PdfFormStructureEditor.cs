using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using System.Text;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

public static partial class PdfFormEditor
{
    /// <summary>Moves one widget, using its zero-based index in the extracted field. Preserves its appearance and field value.</summary>
    public static void MoveWidget(PdfObjectDocument document, string fieldName, int widgetIndex, double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
        {
            throw new ArgumentOutOfRangeException(nameof(deltaX));
        }

        PdfPageEditor.Edit(document, working =>
        {
            var form = new PdfFormGraph(working);
            var matches = form.Fields.Where(f => f.Model.Name == fieldName).ToList();
            if (matches.Count != 1 || widgetIndex < 0 || widgetIndex >= matches[0].Widgets.Count)
            {
                throw new ArgumentException("Choose a valid field widget.");
            }

            var field = matches[0];
            if (field.Model.IsReadOnly || form.Fields.Any(f => f.Model.Kind == PdfFormFieldKind.Signature && f.Inherited.ContainsKey("V")))
            {
                throw new NotSupportedException("Cannot move read-only fields or widgets in signed forms.");
            }

            var widget = field.Widgets[widgetIndex];
            var rectangle = form.Graph.Get(widget, "Rect")?.Items?.Select(v => Number(form.Graph.Resolve(v))).ToArray();
            if (rectangle is not { Length: 4 })
            {
                throw new InvalidDataException("Widget rectangle is missing.");
            }

            var moved = new[] { rectangle[0] + deltaX, rectangle[1] + deltaY, rectangle[2] + deltaX, rectangle[3] + deltaY };
            if (moved.Any(v => !double.IsFinite(v)))
            {
                throw new ArgumentOutOfRangeException(nameof(deltaX));
            }

            widget.Entries!["Rect"] = Array(moved.Select(Num).ToArray());
        });
    }

    /// <summary>Renames a terminal field's partial name, preserving its parent, values and widgets.</summary>
    public static void RenameField(PdfObjectDocument document, string fieldName, string newName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (newName.Contains('.') || newName.Any(char.IsControl))
        {
            throw new ArgumentException("Use a partial field name without dots or control characters.", nameof(newName));
        }

        var encoded = new byte[] { 0xFE, 0xFF }.Concat(new UnicodeEncoding(true, false, true).GetBytes(newName)).ToArray();
        PdfPageEditor.Edit(document, working =>
        {
            var form = new PdfFormGraph(working);
            var matches = form.Fields.Where(f => f.Model.Name == fieldName).ToList();
            if (matches.Count != 1)
            {
                throw new ArgumentException("Field name must identify exactly one existing terminal field.", nameof(fieldName));
            }

            var field = matches[0];
            if (field.Model.IsReadOnly)
            {
                throw new InvalidOperationException("The field is read-only.");
            }

            if (form.Fields.Any(f => f.Model.Kind == PdfFormFieldKind.Signature && f.Inherited.ContainsKey("V")))
            {
                throw new NotSupportedException("Renaming fields in signed forms is not supported.");
            }

            bool HasNameDependentAction(PdfValue value)
            {
                var entries = value.Entries;
                return entries is not null && (entries.ContainsKey("JS") || entries.ContainsKey("JavaScript")
                    || entries.TryGetValue("S", out var action) && form.Graph.Resolve(action).Name is "JavaScript" or "SubmitForm" or "ResetForm" or "Hide" or "ImportData")
                    || (entries?.Values.Any(HasNameDependentAction) ?? false)
                    || (value.Items?.Any(HasNameDependentAction) ?? false);
            }

            if (working.Objects.Any(o => HasNameDependentAction(o.Value)) || HasNameDependentAction(working.Trailer))
            {
                throw new NotSupportedException("Renaming forms with JavaScript or name-dependent form actions is not supported.");
            }

            if (form.Graph.Get(field.Node, "T") is not { } title || PdfFormGraph.Text(title).Contains('.'))
            {
                throw new NotSupportedException("Renaming requires an explicit valid partial field name.");
            }

            var newFullName = fieldName[..(fieldName.LastIndexOf('.') + 1)] + newName;
            if (form.Fields.Any(f => !ReferenceEquals(f, field) && (f.Model.Name == newFullName || f.Model.Name.StartsWith(newFullName + ".", StringComparison.Ordinal))))
            {
                throw new ArgumentException("The new name conflicts with an existing field or field group.", nameof(newName));
            }

            field.Node.Entries!["T"] = Str(encoded);
        });
    }

    /// <summary>Deletes a terminal field and all of its page widgets. Orphan objects are retained.</summary>
    public static void DeleteField(PdfObjectDocument document, string fieldName)
    {
        PdfPageEditor.Edit(document, working =>
        {
            var form = new PdfFormGraph(working);
            var field = form.Fields.Single(f => f.Model.Name == fieldName);
            if (field.Model.IsReadOnly || form.Fields.Any(f => f.Model.Kind == PdfFormFieldKind.Signature && f.Inherited.ContainsKey("V"))
                || form.Graph.Get(form.AcroForm!, "CO")?.Items?.Count > 0)
            {
                throw new NotSupportedException("Read-only fields, signed forms and calculation-order forms cannot be deleted.");
            }

            foreach (var page in form.Graph.Pages())
            {
                if (form.Graph.Get(page.Dictionary, "Annots")?.Items is { } annotations)
                {
                    page.Dictionary.Entries!["Annots"] = new() { Type = "array", Items = annotations.Where(a => !field.Widgets.Contains(form.Graph.Resolve(a))).ToList() };
                }
            }

            bool Remove(PdfValue node)
            {
                node = form.Graph.Resolve(node);
                if (ReferenceEquals(node, field.Node))
                {
                    return true;
                }

                if (form.Graph.Get(node, "Kids")?.Items is { } children)
                {
                    var retained = children.Where(c => !Remove(c)).ToList();
                    node.Entries!["Kids"] = new() { Type = "array", Items = retained };
                    return children.Count > 0 && retained.Count == 0;
                }

                return false;
            }

            var roots = form.Graph.Get(form.AcroForm!, "Fields")!.Items!;
            form.AcroForm!.Entries!["Fields"] = new() { Type = "array", Items = roots.Where(r => !Remove(r)).ToList() };
        });
    }

    /// <summary>Adds a top-level text field (optionally multiline) and its widget, using the default field appearance.</summary>
    public static void InsertTextField(PdfObjectDocument document, int pageNumber, string fieldName, string value, double x, double y, double width, double height, PdfTextAlignment textAlignment = PdfTextAlignment.Left, bool multiline = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        if (fieldName.Contains('.') || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0 || !double.IsFinite(x + width) || !double.IsFinite(y + height))
        {
            throw new ArgumentException("Use a top-level field name without dots and finite positive dimensions.");
        }

        PdfPageEditor.Edit(document, working =>
        {
            var form = new PdfFormGraph(working);
            if (form.Fields.Any(f => f.Model.Name == fieldName || f.Model.Name.StartsWith(fieldName + ".", StringComparison.Ordinal)))
            {
                throw new ArgumentException("The field name already exists.");
            }

            var graph = form.Graph;
            var page = graph.Pages()[pageNumber - 1];
            var acro = form.AcroForm ?? Dict(("Fields", Array()));
            graph.Resolve(working.Trailer.Entries!["Root"]).Entries!["AcroForm"] = acro;
            var widget = Dict(("Type", Name("Annot")), ("Subtype", Name("Widget")), ("FT", Name("Tx")),
                ("T", Str(new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(fieldName)).ToArray())),
                ("Ff", Num(multiline ? 1 << 12 : 0)), ("F", Num(4)), ("Rect", Array(Num(x), Num(y), Num(x + width), Num(y + height))));
            var id = new PdfReference(checked(working.Objects.Max(o => o.Id.ObjectNumber) + 1));
            working.Objects.Add(new(id, widget));
            acro.Entries!["Fields"] = new() { Type = "array", Items = [.. graph.Get(acro, "Fields")?.Items ?? [], Ref(id)] };
            page.Dictionary.Entries!["Annots"] = new() { Type = "array", Items = [.. graph.Get(page.Dictionary, "Annots")?.Items ?? [], Ref(id)] };
            SetValue(working, fieldName, value, textAlignment);
        });
    }
}
