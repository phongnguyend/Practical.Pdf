using PdfObjectKit.Validators;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
namespace PdfObjectKit.Readers;

/// <summary>Reads and validates a PDF object graph.</summary>
public static class PdfReader
{
    public static PdfObjectDocument Read(byte[] bytes)
    {
        var result = PdfNativeParser.Parse(bytes);
        PdfDocumentValidator.Validate(result);
        return result;
    }
}
