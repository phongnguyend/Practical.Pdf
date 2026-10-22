namespace PdfObjectKit.Models;

public enum PdfFormFieldKind
{
    Unknown, Text, CheckBox, RadioButton, PushButton, Choice, Signature
}

public enum PdfTextAlignment
{
    Left = 0,
    Center = 1,
    Right = 2
}

public sealed class PdfFormField
{
    public required string Name { get; init; }

    public PdfReference? ObjectReference { get; init; }

    public PdfFormFieldKind Kind { get; init; }

    public PdfTextAlignment TextAlignment { get; init; }

    public int Flags { get; init; }

    public bool IsReadOnly => (Flags & 1) != 0;

    public bool IsRequired => (Flags & 2) != 0;

    public List<string> Values { get; init; } = [];

    public List<string> DefaultValues { get; init; } = [];

    public List<PdfFormChoice> Choices { get; init; } = [];

    public List<PdfFormWidget> Widgets { get; init; } = [];
}

public sealed record PdfFormChoice(string ExportValue, string DisplayValue);

public sealed class PdfFormWidget
{
    public PdfReference? ObjectReference { get; init; }

    public int? PageNumber { get; init; }

    public List<double> Rectangle { get; init; } = [];

    public List<string> AppearanceStates { get; init; } = [];
}
