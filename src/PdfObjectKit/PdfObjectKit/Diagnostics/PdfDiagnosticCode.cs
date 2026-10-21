using System.Text.Json.Serialization;

namespace PdfObjectKit.Diagnostics;

/// <summary>Stable machine-readable identifiers. Messages may change independently.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PdfDiagnosticCode>))]
public enum PdfDiagnosticCode
{
    ImagesOmitted = 1001,
    ImageExtractionIncomplete = 1002,
    OcrRequired = 1003,
    TextExtractionFailed = 2001,
    UnsupportedTextLayout = 2002,
    AmbiguousReadingOrder = 3001
}
