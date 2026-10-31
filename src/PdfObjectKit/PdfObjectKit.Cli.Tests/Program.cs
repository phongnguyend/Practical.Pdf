using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PdfObjectKit.Cli;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;


static PdfValue Dict(params (string Key, PdfValue Value)[] entries) => new() { Type = "dictionary", Entries = entries.ToDictionary(entry => entry.Key, entry => entry.Value) };
static PdfValue Name(string name) => new() { Type = "name", Name = name };
static PdfValue Num(int number) => new() { Type = "number", Number = number.ToString(System.Globalization.CultureInfo.InvariantCulture) };
static PdfValue Ref(PdfReference reference) => new() { Type = "reference", Reference = reference };
static PdfValue Array(params PdfValue[] values) => new() { Type = "array", Items = values.ToList() };

var directory = Path.Combine(Path.GetTempPath(), "pdfobjectkit-cli-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var checks = 0;
string At(string name) => Path.Combine(directory, name);
void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception(name);
    }
    checks++;
}
async Task Run(params string[] command)
{
    using var log = new StringWriter();
    using var errors = new StringWriter();
    var code = await CliApplication.Run(command, log, errors);
    Check(code == 0, string.Join(' ', command) + "\n" + errors);
}
async Task Reject(string expected, params string[] command)
{
    using var errors = new StringWriter();
    var code = await CliApplication.Run(command, TextWriter.Null, errors);
    Check(code != 0 && errors.ToString().Contains(expected, StringComparison.OrdinalIgnoreCase), $"Expected rejection '{expected}': {errors}");
}
string Settings(string name, object data)
{
    var file = At(name);
    File.WriteAllText(file, JsonSerializer.Serialize(data));
    return file;
}
PdfObjectDocument Read(string name) => PdfReader.Read(File.ReadAllBytes(At(name)));
async Task Edit(string input, string output, object edit, params string[] extra)
{
    await Run(["edit", At(input), At(output), "--options", Settings("edit.json", edit), .. extra]);
}

try
{
    var document = new PdfObjectDocument { Trailer = Dict(("Root", Ref(new(1)))) };
    document.Objects.Add(new(new(1), Dict(("Type", Name("Catalog")), ("Pages", Ref(new(2))))));
    document.Objects.Add(new(new(2), Dict(("Type", Name("Pages")), ("Kids", Array(Ref(new(3)), Ref(new(4)))), ("Count", Num(2)))));
    document.Objects.Add(new(new(3), Dict(("Type", Name("Page")), ("Parent", Ref(new(2))), ("MediaBox", Array(Num(0), Num(0), Num(300), Num(200))))));
    document.Objects.Add(new(new(4), Dict(("Type", Name("Page")), ("Parent", Ref(new(2))), ("MediaBox", Array(Num(0), Num(0), Num(300), Num(200))))));
    PdfPageEditor.InsertText(document, 1, "Hello world", 20, 100, 18);
    PdfPageEditor.InsertText(document, 2, "Second page", 20, 100, 18);
    File.WriteAllBytes(At("input.pdf"), PdfWriter.Write(document));
    await Run("--help");
    await Run("inspect", At("input.pdf"), At("inspect.json"));
    using (var inspection = JsonDocument.Parse(File.ReadAllText(At("inspect.json"))))
    {
        Check(inspection.RootElement.GetProperty("objects")[0].GetProperty("details").GetProperty("baseline").GetDouble() == 100, "Inspector geometry matches Playground");
    }
    await Run("to-json", At("input.pdf"), At("graph.json"));
    Check(File.Exists(At("graph.pagaraphs.json")), "Legacy sidecar filename preserved");
    await Run("to-pdf", At("graph.json"), At("rebuilt.pdf"));
    Check(PdfTextExtractor.Extract(Read("rebuilt.pdf"))[0].Text.Contains("Hello world"), "JSON round trip");
    await Run("to-markdown", At("input.pdf"), At("document.md"), "--options", Settings("markdown.json", new { includeImages = false, joinHyphenatedWords = true }));
    Check(File.ReadAllText(At("document.md")).Contains("Hello world"), "Markdown text");
    await Run("search", At("input.pdf"), At("matches.json"), "--query", "world");
    using (var matches = JsonDocument.Parse(File.ReadAllText(At("matches.json"))))
    {
        Check(matches.RootElement.GetProperty("matches").GetArrayLength() == 1, "Search matches");
    }
    await Reject("Unknown option", "inspect", At("input.pdf"), At("bad.json"), "--typo", "true");
    await Reject("Duplicate option", "inspect", At("input.pdf"), At("bad.json"), "--overwrite", "--overwrite");
    await Reject("could not be mapped", "flatten", At("input.pdf"), At("bad.pdf"), "--options", Settings("bad-options.json", new { typo = true }));
    Check(!File.Exists(At("bad.pdf")), "Invalid options publish no output");
    await Reject("already exists", "inspect", At("input.pdf"), At("inspect.json"));
    var original = File.ReadAllBytes(At("input.pdf"));
    await Reject("different paths", "rotate-page", At("input.pdf"), At("input.pdf"), "--page", "1", "--degrees", "90", "--overwrite");
    Check(original.SequenceEqual(File.ReadAllBytes(At("input.pdf"))), "Input remains unchanged");
    await Run("inspect", At("input.pdf"), At("inspect.json"), "--overwrite");
    await Run("split", At("input.pdf"), At("split"));
    Check(Directory.GetFiles(At("split"), "*.pdf").Length == 2, "Split output count");
    await Run("extract", At("input.pdf"), At("extracted.pdf"), "--pages", "2");
    Check(PdfTextExtractor.Extract(Read("extracted.pdf"))[0].Text.Contains("Second"), "Page extraction");
    await Run("reorder-pages", At("input.pdf"), At("reordered.pdf"), "--pages", "2,1");
    Check(PdfTextExtractor.Extract(Read("reordered.pdf"))[0].Text.Contains("Second"), "Page order");
    await Run("duplicate-page", At("input.pdf"), At("duplicated.pdf"), "--page", "1");
    Check(PdfImageExtractor.Extract(Read("duplicated.pdf")).Count == 3, "Page duplication");
    await Run("insert-blank-page", At("input.pdf"), At("blank.pdf"), "--page", "last");
    Check(PdfTextExtractor.Extract(Read("blank.pdf"))[2].Text == "", "Blank final page");
    await Run("delete-page", At("input.pdf"), At("deleted.pdf"), "--page", "1");
    Check(PdfImageExtractor.Extract(Read("deleted.pdf")).Count == 1, "Page deletion");
    await Run("rotate-page", At("input.pdf"), At("rotated.pdf"), "--page", "1", "--degrees", "90");
    await Run("merge", Settings("inputs.json", new[] { "input.pdf", "extracted.pdf" }), At("merged.pdf"));
    Check(PdfImageExtractor.Extract(Read("merged.pdf")).Count == 3, "Relative merge input paths");
    await Run("compress", At("input.pdf"), At("compressed.pdf"));
    Check(new FileInfo(At("compressed.pdf")).Length <= original.Length, "Compression no-growth guarantee");
    await Edit("input.pdf", "meta.pdf", new { kind = "metadata", metadata = new { fields = new { Title = "CLI title", Author = "Test" } } });
    Check(PdfMetadataExtractor.Extract(Read("meta.pdf")).Values["Title"] == "CLI title", "Metadata edit");
    await Run("metadata", At("meta.pdf"), At("meta.json"));
    await Edit("input.pdf", "watermarked.pdf", new { kind = "add-watermark", watermark = new { text = "DRAFT" } });
    await Run("watermarks", At("watermarked.pdf"), At("watermarks.json"));
    Check(PdfWatermarkEditor.GetWatermarks(Read("watermarked.pdf")).Count > 0, "Watermark insertion");
    await Edit("input.pdf", "field.pdf", new { kind = "add-field", pageNumber = 1, fieldName = "Name", value = "Alice", x = 20, y = 20, width = 100, height = 25 });
    await Edit("field.pdf", "renamed.pdf", new { kind = "rename-field", fieldName = "Name", value = "Customer" });
    Check(PdfFormExtractor.Extract(Read("renamed.pdf"))[0].Name == "Customer", "Field rename");
    await Run("forms", At("renamed.pdf"), At("forms.json"));
    await Edit("renamed.pdf", "annotated.pdf", new { kind = "add-highlight", pageNumber = 1, x = 20, y = 95, width = 70, height = 20 });
    await Run("annotations", At("annotated.pdf"), At("annotations.json"));
    await Run("flatten", At("annotated.pdf"), At("flat.pdf"));
    Check(PdfFormExtractor.Extract(Read("flat.pdf")).Count == 0 && PdfAnnotationExtractor.Extract(Read("flat.pdf")).Count == 0, "Flattened fields and markup");
    var run = PdfParagraphExtractor.ExtractSelectionLines(Read("input.pdf"), 1)[0].Runs[0];
    await Edit("input.pdf", "text.pdf", new { kind = "text", pageNumber = 1, run, value = "Changed" });
    Check(PdfTextExtractor.Extract(Read("text.pdf"))[0].Text.Contains("Changed"), "Text run edit");
    await Edit("input.pdf", "moved.pdf", new { kind = "move-text", pageNumber = 1, runs = new[] { run }, deltaX = 15, deltaY = 10 });
    Check(PdfParagraphExtractor.ExtractSelectionLines(Read("moved.pdf"), 1)[0].Baseline == 110, "Text movement");
    await Edit("input.pdf", "no-text.pdf", new { kind = "delete-text-line", pageNumber = 1, runs = new[] { run } });
    Check(PdfTextExtractor.Extract(Read("no-text.pdf"))[0].Text == "", "Text deletion");
    var regions = Settings("regions.json", new[] { new { pageNumber = 1, x = 15, y = 85, width = 130, height = 40 } });
    await Run("redaction-preview", At("input.pdf"), At("preview.json"), "--options", regions);
    await Run("redact", At("input.pdf"), At("redacted.pdf"), "--options", regions);
    Check(!PdfTextExtractor.Extract(Read("redacted.pdf"))[0].Text.Contains("Hello"), "Content redaction");
    var security = Settings("security.json", new { userPassword = "reader", ownerPassword = "owner", allowModifying = false });
    await Run("protect", At("input.pdf"), At("protected.pdf"), "--options", security);
    Environment.SetEnvironmentVariable("PDFOBJECTKIT_CLI_TEST_PASSWORD", "reader");
    await Reject("owner password", "unlock", At("protected.pdf"), At("denied.pdf"), "--password-env", "PDFOBJECTKIT_CLI_TEST_PASSWORD");
    Environment.SetEnvironmentVariable("PDFOBJECTKIT_CLI_TEST_PASSWORD", "owner");
    await Run("unlock", At("protected.pdf"), At("unlocked.pdf"), "--password-env", "PDFOBJECTKIT_CLI_TEST_PASSWORD");
    Check(Read("unlocked.pdf").Security is null, "Security removed with owner authorization");
    await Run("render-data", At("input.pdf"), At("display-list.json"), "--page", "1");
    File.WriteAllBytes(At("pixel.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
    await Edit("input.pdf", "image.pdf", new { kind = "add-image", pageNumber = 1, x = 20, y = 20, width = 30, height = 30 }, "--image-file", At("pixel.png"));
    await Run("image-data", At("image.pdf"), At("image.json"), "--page", "1", "--image", "1");
    await Edit("image.pdf", "image-moved.pdf", new { kind = "move-image", pageNumber = 1, imageNumber = 1, deltaX = 10, deltaY = 20 });
    await Edit("image-moved.pdf", "image-replaced.pdf", new { kind = "replace-image", pageNumber = 1, imageNumber = 1 }, "--image-file", At("pixel.png"));
    await Edit("image-replaced.pdf", "image-deleted.pdf", new { kind = "delete-image", pageNumber = 1, imageNumber = 1 });
    Check(PdfImageExtractor.Extract(Read("image-deleted.pdf"))[0].Images.Count == 0, "Image edit sequence");
    await Edit("input.pdf", "signature-field.pdf", new { kind = "add-field", fieldType = "Signature", fieldName = "Signature", pageNumber = 1 });
    using (var rsa = RSA.Create(2048))
    {
        var certificateRequest = new CertificateRequest("CN=CLI regression", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(At("signing.pfx"), certificate.Export(X509ContentType.Pkcs12, "test-only"));
    }
    Environment.SetEnvironmentVariable("PDFOBJECTKIT_CLI_TEST_CERT", "test-only");
    await Edit("signature-field.pdf", "signed.pdf", new { kind = "sign-field", fieldName = "Signature", value = "Test" }, "--certificate-file", At("signing.pfx"), "--certificate-password-env", "PDFOBJECTKIT_CLI_TEST_CERT");
    Check(PdfFormExtractor.Extract(Read("signed.pdf")).Any(field => field.IsSigned), "Signature created");
    await Reject("signature", "rotate-page", At("signed.pdf"), At("bad-signed.pdf"), "--page", "1", "--degrees", "90");
    if (args.Length == 2 && args[0] == "--visual")
    {
        var helper = Path.GetFullPath(args[1]);
        await Run("render", At("input.pdf"), At("render.png"), "--helper", helper);
        Check(File.ReadAllBytes(At("render.png")).AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG render signature");
        await Run("export-image", At("image.pdf"), At("export.png"), "--image", "1", "--helper", helper);
        await Run("rasterize", At("rotated.pdf"), At("raster.pdf"), "--helper", helper);
        Check(PdfTextExtractor.Extract(Read("raster.pdf")).All(page => page.Text == "") && PdfImageExtractor.Extract(Read("raster.pdf"))[0].Images.Count == 1, "Image-only output removes text");
        await Run("redact-raster", At("input.pdf"), At("raster-redacted.pdf"), "--options", regions, "--helper", helper);
        var redactedPixels = PdfObjectKit.Rendering.PdfCanvasExporter.ExportImage(Read("raster-redacted.pdf"), 1, 1);
        var pixels = Convert.FromBase64String(redactedPixels.Data!);
        var maskedPixel = (160 * redactedPixels.Width + 40) * 4;
        Check(pixels[maskedPixel] == 0 && pixels[maskedPixel + 1] == 0 && pixels[maskedPixel + 2] == 0, "Raster redaction masks the requested PDF-space location");
        await Run("compare", At("input.pdf"), At("comparison"), "--other", At("text.pdf"), "--helper", helper);
        Check(File.Exists(At("comparison/index.html")), "Comparison report");
        await Run("compare", At("input.pdf"), At("identical"), "--other", At("input.pdf"), "--helper", helper);
        using (var comparison = JsonDocument.Parse(File.ReadAllText(At("identical/comparison.json"))))
        {
            Check(comparison.RootElement.EnumerateArray().All(page => page.GetProperty("changedPixels").GetInt32() == 0), "Identical visual comparison");
        }
        File.WriteAllText(At("qr.txt"), "https://example.com");
        await Run("qr", At("qr.txt"), At("qr.png"), "--helper", helper);
        await Run("ocr", At("render.png"), At("ocr.json"), "--helper", helper);
        Check(File.ReadAllText(At("ocr.json")).Contains("Hello"), "OCR recognizes rendered text");
        await Run("rasterize", At("input.pdf"), At("ocr-source.pdf"), "--helper", helper);
        var font = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(helper)!, "../../PdfObjectKit.Playground.Ui/public/fonts/NotoSans-Regular.ttf"));
        await Edit("ocr-source.pdf", "searchable.pdf", new { kind = "apply-image-ocr", pageNumber = 1, imageNumber = 1 }, "--ocr-lines", At("ocr.json"), "--font-file", font);
        Check(PdfTextExtractor.Extract(Read("searchable.pdf"))[0].Text.Contains("Hello"), "OCR JSON applies as searchable PDF text");
        await Reject("region", "redact-raster", At("input.pdf"), At("bad-raster.pdf"), "--options", Settings("bad-region.json", new[] { new { pageNumber = 3, x = 0, y = 0, width = 10, height = 10 } }), "--helper", helper);
        Check(!File.Exists(At("bad-raster.pdf")), "Invalid redaction publishes no output");
    }
    Console.WriteLine($"Passed {checks} CLI checks.");
    return 0;
}
finally
{
    Environment.SetEnvironmentVariable("PDFOBJECTKIT_CLI_TEST_PASSWORD", null);
    Environment.SetEnvironmentVariable("PDFOBJECTKIT_CLI_TEST_CERT", null);
    Directory.Delete(directory, recursive: true);
}
