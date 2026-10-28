using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PdfObjectKit.Models;
using PdfObjectKit.Options;
using PdfObjectKit.Graphs;
using PdfObjectKit.Converters;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Security;

// Standard Security Handler, AESV3 (ISO 32000-2 algorithms 2.B and 8-10).
internal sealed class PdfStandardSecurity : IDisposable
{
    private readonly byte[] key;
    private readonly bool encryptMetadata;
    private readonly bool stringsEncrypted;
    private readonly bool streamsEncrypted;
    private readonly bool embeddedEncrypted;

    private PdfStandardSecurity(byte[] key, bool metadata, bool strings = true, bool streams = true, bool embedded = true)
    {
        this.key = key;
        encryptMetadata = metadata;
        stringsEncrypted = strings;
        streamsEncrypted = streams;
        embeddedEncrypted = embedded;
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(key);

    private static byte[] Password(string value)
    {
        // PDF AES-256 passwords use UTF-8, truncated to 127 bytes (not characters).
        if (value.Length > 4096)
        {
            throw new ArgumentException("Password is too long.");
        }
        var bytes = new UTF8Encoding(false, true).GetBytes(value);
        return bytes.Length > 127 ? bytes[..127] : bytes;
    }

    private static byte[] Cbc(byte[] key, byte[] data, byte[] iv, bool encrypt, PaddingMode padding = PaddingMode.None)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return encrypt ? aes.EncryptCbc(data, iv, padding) : aes.DecryptCbc(data, iv, padding);
    }

    private static byte[] Hash(byte[] password, byte[] salt, byte[] user, int revision)
    {
        var hash = SHA256.HashData([.. password, .. salt, .. user]);
        if (revision == 5)
        {
            return hash;
        }
        for (var round = 0; ; round++)
        {
            byte[] block = [.. password, .. hash, .. user];
            var repeated = new byte[block.Length * 64];
            for (var i = 0; i < 64; i++)
            {
                block.CopyTo(repeated, i * block.Length);
            }
            var encrypted = Cbc(hash[..16], repeated, hash[16..32], true);
            var selector = encrypted.Take(16).Sum(b => (int)b) % 3;
            hash = selector == 0 ? SHA256.HashData(encrypted) : selector == 1 ? SHA384.HashData(encrypted) : SHA512.HashData(encrypted);
            CryptographicOperations.ZeroMemory(repeated);
            CryptographicOperations.ZeroMemory(block);
            if (round >= 63 && encrypted[^1] <= round - 31)
            {
                return hash[..32];
            }
        }
    }

    public static PdfStandardSecurity Open(PdfValue dictionary, Func<PdfValue, PdfValue> resolve, string? password, out PdfSecurityInfo info)
    {
        PdfValue? Get(PdfValue node, string name) => resolve(node).Entries?.GetValueOrDefault(name) is { } value ? resolve(value) : null;
        byte[] Bytes(string name, int length)
        {
            var value = Get(dictionary, name);
            var data = value?.Type == "string" ? Convert.FromBase64String(value.Base64!) : [];
            if (data.Length != length)
            {
                throw new InvalidDataException($"Invalid encryption /{name} value.");
            }
            return data;
        }
        var revision = Number(Get(dictionary, "R") ?? Num(0));
        if (Get(dictionary, "Filter")?.Name != "Standard" || Number(Get(dictionary, "V") ?? Num(0)) != 5 || revision is not (5 or 6)
            || Get(dictionary, "SubFilter") is not null || Number(Get(dictionary, "Length") ?? Num(256)) != 256)
        {
            throw new NotSupportedException("Only Standard AES-256 security revisions 5 and 6 are supported.");
        }
        var metadata = Get(dictionary, "EncryptMetadata")?.Boolean ?? true;
        bool Filter(string name, string fallback)
        {
            var filter = Get(dictionary, name)?.Name ?? fallback;
            if (filter == "Identity")
            {
                return false;
            }
            var cf = Get(dictionary, "CF");
            var entry = cf is null ? null : Get(cf, filter);
            if (entry is null || Get(entry, "CFM")?.Name != "AESV3")
            {
                throw new NotSupportedException("Unsupported PDF crypt filter.");
            }
            return true;
        }
        var strings = Filter("StrF", "Identity");
        var streams = Filter("StmF", "Identity");
        var embedded = Filter("EFF", Get(dictionary, "StmF")?.Name ?? "Identity");
        var u = Bytes("U", 48);
        var o = Bytes("O", 48);
        var p = Password(password ?? "");
        byte[] fileKey;
        bool owner;
        try
        {
            owner = CryptographicOperations.FixedTimeEquals(Hash(p, o[32..40], u, (int)revision), o[..32]);
            var validUser = !owner && CryptographicOperations.FixedTimeEquals(Hash(p, u[32..40], [], (int)revision), u[..32]);
            if (!owner && !validUser)
            {
                throw new PdfPasswordException(password is null);
            }
            var intermediate = Hash(p, owner ? o[40..48] : u[40..48], owner ? u : [], (int)revision);
            try
            {
                fileKey = Cbc(intermediate, Bytes(owner ? "OE" : "UE", 32), new byte[16], false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(intermediate);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(p);
        }
        var permissionNumber = Number(Get(dictionary, "P") ?? throw new InvalidDataException("Missing encryption permissions."));
        if (permissionNumber < int.MinValue || permissionNumber > uint.MaxValue || permissionNumber != Math.Truncate(permissionNumber))
        {
            CryptographicOperations.ZeroMemory(fileKey);
            throw new InvalidDataException("Invalid encryption permissions.");
        }
        var permissionBits = unchecked((int)(long)permissionNumber);
        using var aes = Aes.Create();
        aes.Key = fileKey;
        var permissions = aes.DecryptEcb(Bytes("Perms", 16), PaddingMode.None);
        if (BinaryPrimitives.ReadInt32LittleEndian(permissions) != permissionBits
            || !permissions.AsSpan(4, 4).SequenceEqual(new byte[] { 255, 255, 255, 255 })
            || permissions[8] != (metadata ? (byte)'T' : (byte)'F') || !permissions.AsSpan(9, 3).SequenceEqual("adb"u8))
        {
            CryptographicOperations.ZeroMemory(fileKey);
            throw new InvalidDataException("Encrypted permissions validation failed.");
        }
        info = new(owner, permissionBits, metadata);
        return new(fileKey, metadata, strings, streams, embedded);
    }

    public void Transform(PdfValue value, bool encrypt, Func<PdfValue, PdfValue>? resolve = null, int depth = 0)
    {
        if (depth > 100)
        {
            throw new InvalidDataException("Excessive encrypted object nesting.");
        }
        PdfValue? Get(string name) => value.Entries?.GetValueOrDefault(name) is { } item ? resolve?.Invoke(item) ?? item : null;
        var type = Get("Type")?.Name;
        if (type == "XRef")
        {
            return;
        }
        if (value.Type == "stream" && (Get("Filter")?.Name == "Crypt"
            || Get("Filter")?.Items?.Any(v => (resolve?.Invoke(v) ?? v).Name == "Crypt") == true))
        {
            throw new NotSupportedException("Explicit per-stream Crypt filters are not supported.");
        }
        if ((value.Type == "string" && stringsEncrypted) || (value.Type == "stream" && (type == "EmbeddedFile" ? embeddedEncrypted : streamsEncrypted) && (encryptMetadata || type != "Metadata")))
        {
            var data = Convert.FromBase64String(value.Base64!);
            if (encrypt)
            {
                var iv = RandomNumberGenerator.GetBytes(16);
                value.Base64 = Convert.ToBase64String([.. iv, .. Cbc(key, data, iv, true, PaddingMode.PKCS7)]);
            }
            else
            {
                if (data.Length < 32 || data.Length % 16 != 0)
                {
                    throw new InvalidDataException("Invalid AES stream or string length.");
                }
                value.Base64 = Convert.ToBase64String(Cbc(key, data[16..], data[..16], false, PaddingMode.PKCS7));
            }
        }
        foreach (var child in value.Items ?? [])
        {
            Transform(child, encrypt, resolve, depth + 1);
        }
        foreach (var pair in value.Entries ?? [])
        {
            // Signature byte ranges cover the original file. Signature contents are never encrypted.
            if (pair.Key == "Contents" && type == "Sig")
            {
                continue;
            }
            Transform(pair.Value, encrypt, resolve, depth + 1);
        }
    }

    public static PdfObjectDocument Encrypt(PdfObjectDocument document, PdfEncryptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.OwnerPassword);
        ArgumentNullException.ThrowIfNull(options.UserPassword);
        if (Encoding.UTF8.GetByteCount(options.UserPassword) > 127 || Encoding.UTF8.GetByteCount(options.OwnerPassword) > 127)
        {
            throw new ArgumentException("Export passwords must be at most 127 UTF-8 bytes.");
        }
        if (string.IsNullOrEmpty(options.OwnerPassword) || options.OwnerPassword == options.UserPassword)
        {
            throw new ArgumentException("Use a nonempty owner password different from the user password.");
        }
        if (document.Trailer.Entries!.ContainsKey("Encrypt"))
        {
            throw new InvalidOperationException("Read/decrypt the document before applying encryption.");
        }
        if (PdfObjectKit.Extractors.PdfFormExtractor.Extract(document).Any(field => field.IsSigned))
        {
            throw new NotSupportedException("Encryption would invalidate existing digital signatures.");
        }
        var copy = PdfConverter.FromJson(PdfConverter.ToJson(document));
        copy.PdfVersion = "2.0";
        var graph = new PdfGraph(copy);
        graph.Resolve(copy.Trailer.Entries!["Root"]).Entries!.Remove("Version");
        var permissions = unchecked((int)0xfffff0c0) | 512;
        permissions |= options.AllowPrinting ? 4 : 0;
        permissions |= options.AllowModifying ? 8 : 0;
        permissions |= options.AllowCopying ? 16 : 0;
        permissions |= options.AllowAnnotations ? 32 : 0;
        permissions |= options.AllowFormFilling ? 256 : 0;
        permissions |= options.AllowDocumentAssembly ? 1024 : 0;
        permissions |= options.AllowPrinting && options.AllowHighQualityPrinting ? 2048 : 0;
        using var security = new PdfStandardSecurity(RandomNumberGenerator.GetBytes(32), options.EncryptMetadata);
        (byte[] Entry, byte[] Wrapped) Make(string password, byte[] user)
        {
            var p = Password(password);
            try
            {
                var validationSalt = RandomNumberGenerator.GetBytes(8);
                var keySalt = RandomNumberGenerator.GetBytes(8);
                var intermediate = Hash(p, keySalt, user, 6);
                try
                {
                    return ([.. Hash(p, validationSalt, user, 6), .. validationSalt, .. keySalt], Cbc(intermediate, security.key, new byte[16], true));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(intermediate);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(p);
            }
        }
        var u = Make(options.UserPassword, []);
        var o = Make(options.OwnerPassword, u.Entry);
        var perms = RandomNumberGenerator.GetBytes(16);
        BinaryPrimitives.WriteInt32LittleEndian(perms, permissions);
        perms.AsSpan(4, 4).Fill(255);
        perms[8] = options.EncryptMetadata ? (byte)'T' : (byte)'F';
        "adb"u8.CopyTo(perms.AsSpan(9));
        using var aes = Aes.Create();
        aes.Key = security.key;
        var dictionary = Dict(("Filter", Name("Standard")), ("V", Num(5)), ("R", Num(6)), ("Length", Num(256)),
            ("P", Num(permissions)), ("U", Str(u.Entry)), ("UE", Str(u.Wrapped)), ("O", Str(o.Entry)), ("OE", Str(o.Wrapped)),
            ("Perms", Str(aes.EncryptEcb(perms, PaddingMode.None))), ("EncryptMetadata", new PdfValue { Type = "boolean", Boolean = options.EncryptMetadata }),
            ("StmF", Name("StdCF")), ("StrF", Name("StdCF")),
            ("CF", Dict(("StdCF", Dict(("CFM", Name("AESV3")), ("AuthEvent", Name("DocOpen")), ("Length", Num(32)))))));
        foreach (var obj in copy.Objects)
        {
            security.Transform(obj.Value, true);
        }
        var id = new PdfReference(copy.Objects.Max(obj => obj.Id.ObjectNumber) + 1);
        copy.Objects.Add(new(id, dictionary));
        copy.Trailer.Entries["Encrypt"] = Ref(id);
        copy.Trailer.Entries["ID"] = Array(Str(RandomNumberGenerator.GetBytes(16)), Str(RandomNumberGenerator.GetBytes(16)));
        return copy;
    }
}
