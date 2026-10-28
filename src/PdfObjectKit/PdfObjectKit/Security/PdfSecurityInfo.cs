namespace PdfObjectKit.Security;

/// <summary>No usable password was supplied for an encrypted PDF.</summary>
public sealed class PdfPasswordException : Exception
{
    public PdfPasswordException(bool passwordRequired) : base(passwordRequired ? "A password is required to open this PDF." : "The PDF password is incorrect.")
    {
        PasswordRequired = passwordRequired;
    }

    public bool PasswordRequired { get; }
}

/// <summary>Authentication and declared viewer permissions. Contains no credentials or encryption keys.</summary>
public sealed record PdfSecurityInfo(bool OwnerAuthenticated, int Permissions, bool EncryptMetadata)
{
    public bool AllowPrinting => (Permissions & 4) != 0;

    public bool AllowCopying => (Permissions & 16) != 0;

    public bool AllowModifying => (Permissions & 8) != 0;

    public bool AllowAnnotations => (Permissions & 32) != 0;

    public bool AllowFormFilling => (Permissions & 256) != 0;

    public bool AllowDocumentAssembly => (Permissions & 1024) != 0;

    public bool AllowHighQualityPrinting => AllowPrinting && (Permissions & 2048) != 0;
}
