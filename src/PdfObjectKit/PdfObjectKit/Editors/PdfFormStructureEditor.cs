using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using System.Text;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

public static partial class PdfFormEditor
{
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

    /// <summary>Adds a top-level single-line text field and its widget, using the default field appearance.</summary>
    public static void InsertTextField(PdfObjectDocument document, int pageNumber, string fieldName, string value, double x, double y, double width, double height)
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
                ("F", Num(4)), ("Rect", Array(Num(x), Num(y), Num(x + width), Num(y + height))));
            var id = new PdfReference(checked(working.Objects.Max(o => o.Id.ObjectNumber) + 1));
            working.Objects.Add(new(id, widget));
            acro.Entries!["Fields"] = new() { Type = "array", Items = [.. graph.Get(acro, "Fields")?.Items ?? [], Ref(id)] };
            page.Dictionary.Entries!["Annots"] = new() { Type = "array", Items = [.. graph.Get(page.Dictionary, "Annots")?.Items ?? [], Ref(id)] };
            SetValue(working, fieldName, value);
        });
    }
}
