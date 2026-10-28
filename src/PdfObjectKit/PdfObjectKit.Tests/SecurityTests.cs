using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using PdfObjectKit.Options;
using PdfObjectKit.Security;
using PdfObjectKit.Extractors;
using PdfObjectKit.Converters;
using System.Text;

internal static class SecurityTests
{
    public static void Run()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception(message);
            }
        }
        foreach (var fixture in new[] { "AES-256.pdf", "AES-256-R5.pdf", "AES-256-objects.pdf" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Security", fixture));
            foreach (var password in new string?[] { null, "wrong" })
            {
                try
                {
                    PdfReader.Read(bytes, new() { Password = password });
                    throw new Exception("Invalid password accepted");
                }
                catch (PdfPasswordException ex)
                {
                    Check(ex.PasswordRequired == (password is null), "Typed password error");
                }
            }
            var read = PdfReader.Read(bytes, new() { Password = "reader-password" });
            Check(read.Security is { OwnerAuthenticated: false }, "User authentication");
            if (fixture == "AES-256-objects.pdf")
            {
                Check(read.Security is { EncryptMetadata: false }, "Metadata encryption flag respected");
                var metadata = read.Objects.Single(obj => obj.Value.Entries?.GetValueOrDefault("Type")?.Name == "Metadata").Value;
                Check(Encoding.UTF8.GetString(PdfObjectKit.Parsers.PdfStreamDecoder.Decode(metadata)).Contains("Public metadata"), "Unencrypted metadata survives decryption");
            }
            Check(PdfTextExtractor.Extract(read)[0].Text.Contains("Independent encryption fixture"), "Independent fixture decrypts");
            var owner = PdfReader.Read(bytes, new() { Password = "owner-password" });
            Check(owner.Security is { OwnerAuthenticated: true }, "Owner authentication");
            Check(!PdfReader.Read(PdfWriter.Write(owner)).Trailer.Entries!.ContainsKey("Encrypt"), "Default write is unencrypted");
        }
        var source = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Secret text")));
        var before = PdfConverter.ToJson(source);
        foreach (var user in new[] { "", "reader", "p\u00e4ss\u4e2d" })
        {
            var options = new PdfWriteOptions { Encryption = new() { UserPassword = user, OwnerPassword = "owner", AllowCopying = false, AllowPrinting = false, AllowModifying = false, EncryptMetadata = false } };
            var encrypted = PdfWriter.Write(source, options);
            Check(!Encoding.Latin1.GetString(encrypted).Contains("Secret text"), "Content encrypted");
            var restored = PdfReader.Read(encrypted, new() { Password = user });
            Check(restored.Security is { AllowCopying: false, AllowPrinting: false, AllowModifying: false, EncryptMetadata: false }, "Permissions recovered");
            Check(PdfTextExtractor.Extract(restored)[0].Text.Contains("Secret text"), "Encrypted export round trip");
            Check(PdfReader.Read(encrypted, new() { Password = "owner" }).Security!.OwnerAuthenticated, "Owner opens export");
            Check(!encrypted.SequenceEqual(PdfWriter.Write(source, options)), "Encryption uses fresh salts, key and IVs");
            if (user == "reader")
            {
                File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "security-export.pdf"), encrypted);
            }
        }
        var protectedBytes = PdfWriter.Write(source, new() { Encryption = new() { UserPassword = "reader", OwnerPassword = "owner" } });
        var serialized = Encoding.Latin1.GetString(protectedBytes);
        var permsIndex = serialized.IndexOf("/Perms <", StringComparison.Ordinal) + 8;
        var tampered = serialized[..permsIndex] + (serialized[permsIndex] == '0' ? '1' : '0') + serialized[(permsIndex + 1)..];
        try
        {
            PdfReader.Read(Encoding.Latin1.GetBytes(tampered), new() { Password = "reader" });
            throw new Exception("Tampered permissions accepted");
        }
        catch (InvalidDataException)
        {
            Check(true, "Tampered permissions rejected");
        }
        try
        {
            PdfReader.Read(Encoding.Latin1.GetBytes(serialized.Replace("/AESV3", "/AESV2")), new() { Password = "reader" });
            throw new Exception("Unsupported filter accepted");
        }
        catch (NotSupportedException)
        {
            Check(true, "Unsupported filters fail explicitly");
        }
        Check(before == PdfConverter.ToJson(source), "Encrypted write leaves source unchanged");
        Console.WriteLine("AES-256 security and independent fixture checks passed.");
    }
}
