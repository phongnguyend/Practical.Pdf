namespace PdfObjectKit.Options;

public sealed class PdfReadOptions
{
    public string? Password { get; init; }
}

public sealed class PdfWriteOptions
{
    public PdfEncryptionOptions? Encryption { get; init; }
}

/// <summary>AES-256 revision 6 export settings. Permissions are enforced by cooperating viewers.</summary>
public sealed class PdfEncryptionOptions
{
    public string UserPassword { get; init; } = "";

    public required string OwnerPassword { get; init; }

    public bool AllowPrinting { get; init; } = true;

    public bool AllowHighQualityPrinting { get; init; } = true;

    public bool AllowCopying { get; init; } = true;

    public bool AllowModifying { get; init; } = true;

    public bool AllowAnnotations { get; init; } = true;

    public bool AllowFormFilling { get; init; } = true;

    public bool AllowDocumentAssembly { get; init; } = true;

    public bool EncryptMetadata { get; init; } = true;
}
