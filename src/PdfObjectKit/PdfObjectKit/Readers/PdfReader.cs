using PdfObjectKit.Validators;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
namespace PdfObjectKit.Readers;

/// <summary>Reads and validates a PDF object graph.</summary>
public static class PdfReader
{
    public static PdfObjectDocument Read(byte[] bytes) => Read(bytes, null);

    /// <summary>Reads an encrypted or plain PDF. The returned graph is decrypted; writing requires explicit encryption options.</summary>
    public static PdfObjectDocument Read(byte[] bytes, PdfObjectKit.Options.PdfReadOptions? options)
    {
        var result = PdfNativeParser.Parse(bytes, options?.Password);
        PdfDocumentValidator.Validate(result);
        return result;
    }
}
