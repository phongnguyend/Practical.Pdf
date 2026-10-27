using System.Text;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Editors;

public static partial class PdfFormEditor
{
    /// <summary>Configures a pushbutton action, or removes it when null. Actions are serialized, never executed by this library.</summary>
    public static void SetButtonAction(PdfObjectDocument document, string fieldName, PdfButtonAction? action)
    {
        PdfPageEditor.Edit(document, working =>
        {
            var form = new PdfFormGraph(working);
            EnsureAppearanceFieldEditable(form);
            var field = form.Fields.SingleOrDefault(f => f.Model.Name == fieldName) ?? throw new ArgumentException("Field not found.");
            if (field.Model.Kind != PdfFormFieldKind.PushButton || field.Model.IsReadOnly)
            {
                throw new ArgumentException("Choose a writable pushbutton field.");
            }
            PdfValue? dictionary = null;
            if (action is not null)
            {
                PdfValue Text(string value) => Str(new byte[] { 254, 255 }.Concat(Encoding.BigEndianUnicode.GetBytes(value)).ToArray());
                switch (action.Kind)
                {
                    case PdfButtonActionKind.OpenUrl:
                    case PdfButtonActionKind.SubmitForm:
                        if (!Uri.TryCreate(action.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo))
                        {
                            throw new ArgumentException("Use an absolute HTTP(S) URL without embedded credentials.");
                        }
                        dictionary = action.Kind == PdfButtonActionKind.OpenUrl
                            ? Dict(("S", Name("URI")), ("URI", Str(Encoding.ASCII.GetBytes(url.AbsoluteUri))))
                            : Dict(("S", Name("SubmitForm")), ("F", Dict(("Type", Name("Filespec")), ("FS", Name("URL")), ("F", Str(Encoding.ASCII.GetBytes(url.AbsoluteUri))))), ("Flags", Num(32)));
                        break;
                    case PdfButtonActionKind.GoToPage:
                        var pages = form.Graph.Pages();
                        if (action.PageNumber < 1 || action.PageNumber > pages.Count)
                        {
                            throw new ArgumentOutOfRangeException(nameof(action.PageNumber));
                        }
                        var page = pages[action.PageNumber - 1];
                        var id = working.Objects.Single(o => ReferenceEquals(o.Value, page.Dictionary)).Id;
                        dictionary = Dict(("S", Name("GoTo")), ("D", Array(Ref(id), Name("Fit"))));
                        break;
                    case PdfButtonActionKind.ResetForm:
                        dictionary = Dict(("S", Name("ResetForm")));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(action.Kind));
                }
                if (action.FieldNames is not null)
                {
                    if (action.Kind is not (PdfButtonActionKind.ResetForm or PdfButtonActionKind.SubmitForm)
                        || action.FieldNames.Count == 0 || action.FieldNames.Distinct().Count() != action.FieldNames.Count
                        || action.FieldNames.Any(name => !form.Fields.Any(f => f.Model.Name == name)))
                    {
                        throw new ArgumentException("Field names must be unique existing fields for ResetForm or SubmitForm; omit the list to target all fields.");
                    }
                    dictionary.Entries!["Fields"] = Array(action.FieldNames.Select(Text).ToArray());
                }
            }
            field.Node.Entries!.Remove("A");
            foreach (var widget in field.Widgets)
            {
                widget.Entries!.Remove("A");
                if (dictionary is not null)
                {
                    widget.Entries["A"] = dictionary;
                }
            }
        });
    }
}
