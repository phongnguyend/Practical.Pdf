using System.Text;
using PdfObjectKit.Models;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Graphs;

internal sealed class PdfFormGraph
{
    internal sealed record Field(PdfFormField Model, PdfValue Node, Dictionary<string, PdfValue> Inherited, List<PdfValue> Widgets);
    internal PdfGraph Graph { get; }

    internal PdfValue? AcroForm { get; }

    internal List<Field> Fields { get; } = [];

    internal PdfFormGraph(PdfObjectDocument document)
    {
        Graph = new(document);
        var catalog = Graph.Resolve(document.Trailer.Entries!["Root"]);
        AcroForm = Graph.Get(catalog, "AcroForm");
        if (AcroForm is null)
        {
            return;
        }

        if (Graph.Get(AcroForm, "XFA") is not null)
        {
            throw new NotSupportedException("XFA forms are not supported.");
        }

        var pages = new Dictionary<PdfValue, int>();
        foreach (var page in Graph.Pages())
        {
            foreach (var annotation in Graph.Get(page.Dictionary, "Annots")?.Items ?? [])
            {
                pages.TryAdd(Graph.Resolve(annotation), page.Number);
            }
        }

        var seen = new HashSet<PdfValue>();
        void Walk(PdfValue raw, string prefix, Dictionary<string, PdfValue> inherited, int depth)
        {
            var node = Graph.Resolve(raw);
            if (depth > 100 || !seen.Add(node))
            {
                throw new InvalidDataException("Cyclic, repeated, or excessively nested form field.");
            }

            var values = new Dictionary<string, PdfValue>(inherited);
            foreach (var key in new[] { "FT", "Ff", "V", "DV", "DA", "Q", "MaxLen", "Opt", "RV" })
            {
                if (Graph.Get(node, key) is { } value)
                {
                    values[key] = value;
                }
            }

            var partial = Graph.Get(node, "T") is { } title ? Text(title) : "";
            var name = prefix.Length == 0 ? partial : partial.Length == 0 ? prefix : prefix + "." + partial;
            var kids = Graph.Get(node, "Kids")?.Items ?? [];
            var childFields = kids.Where(k => Graph.Get(k, "Subtype")?.Name != "Widget" || Graph.Get(k, "T") is not null || Graph.Get(k, "FT") is not null).ToList();
            if (childFields.Count > 0)
            {
                if (childFields.Count != kids.Count)
                {
                    throw new InvalidDataException("Mixed field and widget children.");
                }

                foreach (var child in childFields)
                {
                    Walk(child, name, values, depth + 1);
                }

                return;
            }

            var widgets = new List<PdfValue>();
            var widgetModels = new List<PdfFormWidget>();
            foreach (var widgetRaw in Graph.Get(node, "Subtype")?.Name == "Widget" ? new List<PdfValue> { raw } : kids)
            {
                var widget = Graph.Resolve(widgetRaw);
                widgets.Add(widget);
                widgetModels.Add(new()
                {
                    ObjectReference = widgetRaw.Reference,
                    PageNumber = pages.TryGetValue(widget, out var number) ? number : null,
                    Rectangle = Graph.Get(widget, "Rect")?.Items?.Select(v => Number(Graph.Resolve(v))).ToList() ?? [],
                    Action = ReadAction(Graph.Get(widget, "A") ?? Graph.Get(node, "A")),
                    Caption = Graph.Get(widget, "MK") is { } mk && Graph.Get(mk, "CA") is { } caption ? Text(caption) : null,
                    AppearanceStates = Graph.Get(widget, "AP") is { } ap && Graph.Get(ap, "N") is { Type: "dictionary" } states
                        ? states.Entries!.Keys.ToList() : []
                });
            }

            var flags = values.TryGetValue("Ff", out var ff) ? checked((int)Number(ff)) : 0;
            var kind = values.GetValueOrDefault("FT")?.Name switch
            {
                "Tx" => PdfFormFieldKind.Text,
                "Ch" => PdfFormFieldKind.Choice,
                "Sig" => PdfFormFieldKind.Signature,
                "Btn" when (flags & (1 << 16)) != 0 => PdfFormFieldKind.PushButton,
                "Btn" when (flags & (1 << 15)) != 0 => PdfFormFieldKind.RadioButton,
                "Btn" => PdfFormFieldKind.CheckBox,
                _ => PdfFormFieldKind.Unknown
            };
            var choices = new List<PdfFormChoice>();
            if (kind == PdfFormFieldKind.Choice)
            {
                foreach (var optionRaw in values.GetValueOrDefault("Opt")?.Items ?? [])
                {
                    var option = Graph.Resolve(optionRaw);
                    if (option.Items is { Count: 2 } pair)
                    {
                        choices.Add(new(Text(Graph.Resolve(pair[0])), Text(Graph.Resolve(pair[1]))));
                    }
                    else
                    {
                        choices.Add(new(Text(option), Text(option)));
                    }
                }
            }

            Fields.Add(new(new PdfFormField
            {
                Name = name, ObjectReference = raw.Reference, Kind = kind, Flags = flags,
                RichText = values.GetValueOrDefault("RV") is { Type: "string" } rich ? Text(rich) : null,
                IsSigned = kind == PdfFormFieldKind.Signature && values.ContainsKey("V"),
                MaxLength = values.TryGetValue("MaxLen", out var maxLength) && Number(maxLength) is > 0 and <= int.MaxValue
                    && Number(maxLength) == Math.Truncate(Number(maxLength)) ? (int)Number(maxLength) : null,
                Values = ReadValues(values.GetValueOrDefault("V")), DefaultValues = ReadValues(values.GetValueOrDefault("DV")),
                TextAlignment = values.TryGetValue("Q", out var alignment) && Number(alignment) is 1 or 2
                    ? (PdfTextAlignment)(int)Number(alignment) : PdfTextAlignment.Left,
                Choices = choices, Widgets = widgetModels
            }, node, values, widgets));
        }

        var defaults = new Dictionary<string, PdfValue>();
        foreach (var key in new[] { "DA", "Q" })
        {
            if (Graph.Get(AcroForm, key) is { } value)
            {
                defaults[key] = value;
            }
        }

        foreach (var root in Graph.Get(AcroForm, "Fields")?.Items ?? [])
        {
            Walk(root, "", defaults, 0);
        }
    }

    private PdfButtonAction? ReadAction(PdfValue? action)
    {
        if (action is null)
        {
            return null;
        }
        var names = Graph.Get(action, "Fields")?.Items?.Select(v => Text(Graph.Resolve(v))).ToList();
        switch (Graph.Get(action, "S")?.Name)
        {
            case "URI":
                return new(PdfButtonActionKind.OpenUrl) { Url = Graph.Get(action, "URI") is { } uri ? Text(uri) : null };
            case "ResetForm":
                return new(PdfButtonActionKind.ResetForm) { FieldNames = names };
            case "SubmitForm":
                var file = Graph.Get(action, "F");
                var url = file?.Type == "dictionary" ? Graph.Get(file, "F") : file;
                return new(PdfButtonActionKind.SubmitForm) { Url = url is null ? null : Text(url), FieldNames = names };
            case "GoTo":
                var destination = Graph.Get(action, "D")?.Items;
                if (destination is { Count: > 0 })
                {
                    var page = Graph.Resolve(destination[0]);
                    var match = Graph.Pages().FindIndex(p => ReferenceEquals(p.Dictionary, page));
                    if (match >= 0)
                    {
                        return new(PdfButtonActionKind.GoToPage) { PageNumber = match + 1 };
                    }
                }
                break;
        }
        return null;
    }

    private List<string> ReadValues(PdfValue? value)
    {
        if (value is null)
        {
            return [];
        }

        return value.Type switch
        {
            "name" => [value.Name!],
            "string" => [Text(value)],
            "array" => value.Items!.Select(v => Text(Graph.Resolve(v))).ToList(),
            _ => [] // Signature dictionaries are not text values.
        };
    }

    internal static string Text(PdfValue value)
    {
        var bytes = Convert.FromBase64String(value.Base64 ?? throw new InvalidDataException("Expected PDF text string."));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
        }

        const string accents = "˘ˇˆ˙˝˛˚˜";
        const string special = "•†‡…—–ƒ⁄‹›−‰„“”‘’‚™ﬁﬂŁŒŠŸŽıłœšž";
        return string.Concat(bytes.Select(b => b is >= 0x18 and <= 0x1F ? accents[b - 0x18]
            : b is >= 0x80 and <= 0x9E ? special[b - 0x80] : b == 0xA0 ? '€' : (char)b));
    }
}
