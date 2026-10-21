using PdfObjectKit.Indexers;
using PdfObjectKit.Parsers;
namespace PdfObjectKit;

/// <summary>Reads a PDF object graph and optionally builds text and image indexes.</summary>
public static class PdfReader
{
    public static PdfObjectDocument Read(byte[] bytes, bool includeIndexes = true)
    {
        var result = PdfNativeParser.Parse(bytes);
        PdfDocumentValidator.Validate(result);
        if (includeIndexes)
        {
            result.TextIndex = PdfTextIndexer.Build(result);
            result.ImageIndex = PdfImageIndexer.Build(result);
        }

        return result;
    }
}
