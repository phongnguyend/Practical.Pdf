namespace PdfObjectKit.Models;

public enum PdfButtonActionKind { OpenUrl, GoToPage, ResetForm, SubmitForm }

public sealed record PdfButtonAction(PdfButtonActionKind Kind)
{
    public string? Url { get; init; }

    public int PageNumber { get; init; } = 1;

    public List<string>? FieldNames { get; init; }
}
