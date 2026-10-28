using PdfObjectKit.Options;
namespace PdfObjectKit.Models;

public sealed record PdfWatermarkInfo(string Id, IReadOnlyList<int> PageNumbers, PdfTextWatermarkOptions Options);
