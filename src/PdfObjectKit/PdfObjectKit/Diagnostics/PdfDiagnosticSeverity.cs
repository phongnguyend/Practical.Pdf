using System.Text.Json.Serialization;

namespace PdfObjectKit.Diagnostics;

[JsonConverter(typeof(JsonStringEnumConverter<PdfDiagnosticSeverity>))]
public enum PdfDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}
