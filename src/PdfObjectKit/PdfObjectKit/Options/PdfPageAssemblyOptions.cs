namespace PdfObjectKit.Options;

/// <summary>Limits and explicit conflict policies for page assembly. Limits apply to each assembly result.</summary>
public sealed record PdfPageAssemblyOptions
{
    public int MaxPages { get; init; } = 10000;

    public int MaxObjects { get; init; } = 1000000;

    public int MaxNodes { get; init; } = 2000000;

    public long MaxEncodedStreamBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Prefix root field names with Document1_, Document2_, etc. Documents containing JavaScript are rejected in this mode.</summary>
    public bool NamespaceFormFields { get; init; }

    /// <summary>Give colliding embedded-file name-tree keys a source prefix. Embedded file bytes and file specifications are unchanged.</summary>
    public bool RenameConflictingAttachments { get; init; }

    /// <summary>Give colliding named destinations a source prefix; local links still resolve to explicit page destinations.</summary>
    public bool RenameConflictingDestinations { get; init; }

    internal void Validate()
    {
        if (MaxPages < 1 || MaxObjects < 2 || MaxNodes < 1 || MaxEncodedStreamBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PdfPageAssemblyOptions), "Assembly limits must be positive (stream bytes may be zero).");
        }
    }
}
