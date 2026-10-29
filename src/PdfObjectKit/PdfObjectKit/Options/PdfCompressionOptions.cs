namespace PdfObjectKit.Options;

/// <summary>Lossless optimization. Image resolution, fonts and document features are preserved.</summary>
public sealed class PdfCompressionOptions
{
    public bool RemoveUnusedObjects { get; init; } = true;

    public bool CompressStreams { get; init; } = true;

    public bool DeduplicateResources { get; init; } = true;

    /// <summary>Return the original bytes if optimization does not reduce file size.</summary>
    public bool KeepOriginalIfLarger { get; init; } = true;

    /// <summary>Total decoded stream bytes processed during optimization.</summary>
    public long MaxDecodedBytes { get; init; } = 512L * 1024 * 1024;
}
