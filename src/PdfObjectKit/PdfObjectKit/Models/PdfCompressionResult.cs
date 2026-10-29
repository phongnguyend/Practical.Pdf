namespace PdfObjectKit.Models;

public sealed record PdfCompressionResult(
    byte[] Bytes,
    int OriginalBytes,
    int OptimizedBytes,
    int RemovedObjects,
    int CompressedStreams,
    int DeduplicatedResources,
    bool UsedOriginal,
    IReadOnlyList<string> Notes);
