using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Editors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Writers;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Signing;

/// <summary>Creates a first RSA/SHA-256 detached CMS approval signature. Returns final bytes; does not modify the input graph.</summary>
public static class PdfSigner
{
    public static byte[] Sign(PdfObjectDocument document, string fieldName, X509Certificate2 certificate, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        using var key = certificate.GetRSAPrivateKey() ?? throw new NotSupportedException("An RSA certificate with an accessible private key is required.");
        if (key.KeySize < 2048)
        {
            throw new ArgumentException("Use an RSA key of at least 2048 bits.", nameof(certificate));
        }
        var now = DateTimeOffset.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
        {
            throw new ArgumentException("The signing certificate is outside its validity period.", nameof(certificate));
        }
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (usage is not null && (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
        {
            throw new ArgumentException("Certificate key usage does not permit signing.", nameof(certificate));
        }
        var working = PdfConverter.FromJson(PdfConverter.ToJson(document));
        var form = new PdfFormGraph(working);
        var catalog = form.Graph.Resolve(working.Trailer.Entries!["Root"]);
        if (form.Graph.Get(catalog, "Perms") is not null || form.Fields.Any(f => f.Model.Kind == PdfFormFieldKind.Signature && f.Inherited.ContainsKey("V")))
        {
            throw new NotSupportedException("Additional signatures require incremental updates and are not supported.");
        }
        var matches = form.Fields.Where(f => f.Model.Name == fieldName).ToList();
        if (matches.Count != 1 || matches[0].Model.Kind != PdfFormFieldKind.Signature || matches[0].Model.IsReadOnly)
        {
            throw new ArgumentException("Choose one writable unsigned signature field.", nameof(fieldName));
        }
        var field = matches[0];
        if (field.Node.Entries!.ContainsKey("Lock") || field.Node.Entries.ContainsKey("SV") || form.Graph.Get(form.AcroForm!, "CO")?.Items?.Count > 0)
        {
            throw new NotSupportedException("Signature seed values, locks and calculation-order forms are unsupported.");
        }
        foreach (var widget in field.Widgets)
        {
            var rect = form.Graph.Get(widget, "Rect")?.Items?.Select(v => Number(form.Graph.Resolve(v))).ToArray();
            if (rect is not { Length: 4 } || rect.Any(v => !double.IsFinite(v)) || rect[2] <= rect[0] || rect[3] <= rect[1]
                || form.Graph.Get(widget, "MK") is { } mk && form.Graph.Number(mk, "R") != 0)
            {
                throw new NotSupportedException("Signing requires nonrotated, positive widget rectangles.");
            }
            PdfFormEditor.SetSimpleFieldAppearance(working, form, widget, "Digitally signed", rect[2] - rect[0], rect[3] - rect[1], false);
        }
        const int capacity = 32768;
        var contents = Str(new byte[capacity]);
        var range = Array(Num(9999999999), Num(9999999999), Num(9999999999), Num(9999999999));
        PdfValue Text(string text) => Str(new byte[] { 254, 255 }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray());
        var signature = Dict(("Type", Name("Sig")), ("Filter", Name("Adobe.PPKLite")), ("SubFilter", Name("adbe.pkcs7.detached")),
            ("ByteRange", range), ("Contents", contents), ("M", Str(Encoding.ASCII.GetBytes(now.ToString("'D:'yyyyMMddHHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture)))),
            ("Name", Text(certificate.GetNameInfo(X509NameType.SimpleName, false))));
        if (!string.IsNullOrEmpty(reason))
        {
            signature.Entries!["Reason"] = Text(reason);
        }
        var id = new PdfReference(checked(working.Objects.Max(o => o.Id.ObjectNumber) + 1));
        working.Objects.Add(new(id, signature));
        field.Node.Entries["V"] = Ref(id);
        form.AcroForm!.Entries!["SigFlags"] = Num(3);
        long contentsStart = -1, contentsLength = 0, rangeStart = -1, rangeLength = 0;
        var bytes = PdfWriter.WriteObjects(working, (value, offset, length) =>
        {
            if (ReferenceEquals(value, contents))
            {
                contentsStart = offset;
                contentsLength = length;
            }
            if (ReferenceEquals(value, range))
            {
                rangeStart = offset;
                rangeLength = length;
            }
        });
        if (contentsStart < 0 || rangeStart < 0)
        {
            throw new InvalidDataException("Signature placeholders were not serialized.");
        }
        var end = checked((int)(contentsStart + contentsLength));
        var rangeText = FormattableString.Invariant($"[0 {contentsStart} {end} {bytes.Length - end}]");
        if (rangeText.Length > rangeLength)
        {
            throw new InvalidDataException("Signature byte range exceeds its reserved space.");
        }
        Encoding.ASCII.GetBytes(rangeText.PadRight((int)rangeLength)).CopyTo(bytes, (int)rangeStart);
        var data = new byte[bytes.Length - (int)contentsLength];
        bytes.AsSpan(0, (int)contentsStart).CopyTo(data);
        bytes.AsSpan(end).CopyTo(data.AsSpan((int)contentsStart));
        var cms = CreateCms(data, certificate, key);
        if (cms.Length > capacity)
        {
            throw new NotSupportedException("Certificate/signature exceeds the reserved CMS size.");
        }
        Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(bytes, (int)contentsStart + 1);
        return bytes;
    }

    // CMS SignedData without signed attributes: the RSA signature covers SHA-256(content).
    private static byte[] CreateCms(byte[] data, X509Certificate2 certificate, RSA key)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        var context = new Asn1Tag(TagClass.ContextSpecific, 0, true);
        void Algorithm(string oid)
        {
            writer.PushSequence();
            writer.WriteObjectIdentifier(oid);
            writer.WriteNull();
            writer.PopSequence();
        }
        writer.PushSequence();
        writer.WriteObjectIdentifier("1.2.840.113549.1.7.2");
        writer.PushSequence(context);
        writer.PushSequence();
        writer.WriteInteger(1);
        writer.PushSetOf();
        Algorithm("2.16.840.1.101.3.4.2.1");
        writer.PopSetOf();
        writer.PushSequence();
        writer.WriteObjectIdentifier("1.2.840.113549.1.7.1");
        writer.PopSequence();
        writer.PushSetOf(context);
        writer.WriteEncodedValue(certificate.RawData);
        writer.PopSetOf(context);
        writer.PushSetOf();
        writer.PushSequence();
        writer.WriteInteger(1);
        writer.PushSequence();
        writer.WriteEncodedValue(certificate.IssuerName.RawData);
        writer.WriteIntegerUnsigned(certificate.GetSerialNumber().Reverse().ToArray());
        writer.PopSequence();
        Algorithm("2.16.840.1.101.3.4.2.1");
        Algorithm("1.2.840.113549.1.1.1");
        writer.WriteOctetString(key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        writer.PopSequence();
        writer.PopSetOf();
        writer.PopSequence();
        writer.PopSequence(context);
        writer.PopSequence();
        return writer.Encode();
    }
}
