using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfObjectKit.Application;
using PdfObjectKit.Converters;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Models;
using PdfObjectKit.Options;
using PdfObjectKit.Readers;
using PdfObjectKit.Rendering;
using PdfObjectKit.Writers;

namespace PdfObjectKit.Cli;

public static class CliApplication
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Run(string[] args, TextWriter output, TextWriter error, CancellationToken cancellation = default)
    {
        try
        {
            if (args.Length == 0 || args is ["--help"] or ["help"])
            {
                output.WriteLine(Help);
                return 0;
            }
            var command = args[0];
            var options = new CliArguments(args.Skip(1));
            var files = new OutputFiles(options.Take("overwrite") is not null);
            if (options.Paths.Count != 2)
            {
                throw new ArgumentException("Expected command, input path and output path. Use --help.");
            }
            var input = files.Input(options.Paths[0]);
            var destination = options.Paths[1];
            var optionsPath = options.Take("options");
            T Settings<T>() where T : new()
            {
                if (optionsPath is null)
                {
                    return new T();
                }
                return ReadJson<T>(files.Input(optionsPath));
            }
            T RequiredSettings<T>()
            {
                return ReadJson<T>(files.Input(optionsPath ?? throw new ArgumentException("Provide --options settings.json.")));
            }
            void SaveJson(object value)
            {
                files.Add(destination, JsonSerializer.SerializeToUtf8Bytes(value, Json));
            }
            PdfObjectDocument ReadPdf(string path)
            {
                var document = PdfReader.Read(File.ReadAllBytes(files.Input(path)));
                if (document.Security is not null)
                {
                    throw new NotSupportedException("Use unlock first, then protect the output again if needed.");
                }
                return document;
            }
            switch (command)
            {
                case "to-json":
                {
                    RejectSettings(optionsPath);
                    var objectsOnly = options.Take("objects-only") is not null;
                    options.Complete();
                    var document = ReadPdf(input);
                    files.Add(destination, Encoding.UTF8.GetBytes(PdfConverter.ToJson(document)));
                    if (!objectsOnly)
                    {
                        files.Add(Path.ChangeExtension(destination, ".texts.json"), Encoding.UTF8.GetBytes(PdfConverter.ToTextsJson(document)));
                        files.Add(Path.ChangeExtension(destination, ".images.json"), Encoding.UTF8.GetBytes(PdfConverter.ToImagesJson(document)));
                        // Preserve the original CLI's sidecar filename for compatibility.
                        files.Add(Path.ChangeExtension(destination, ".pagaraphs.json"), Encoding.UTF8.GetBytes(PdfConverter.ToParagraphsJson(document)));
                    }
                    break;
                }
                case "to-pdf":
                {
                    RejectSettings(optionsPath);
                    options.Complete();
                    files.Add(destination, PdfWriter.Write(PdfConverter.FromJson(File.ReadAllText(input))));
                    break;
                }
                case "to-markdown":
                {
                    var settings = Settings<MarkdownConversionOptions>();
                    options.Complete();
                    var result = PdfMarkdownConverter.Convert(ReadPdf(input), settings.ToConverterOptions());
                    files.Add(destination, Encoding.UTF8.GetBytes(result.Markdown));
                    foreach (var diagnostic in result.Diagnostics)
                    {
                        error.WriteLine($"{diagnostic.Severity} [{diagnostic.Code}]: {diagnostic.Message}");
                    }
                    break;
                }
                case "inspect":
                case "metadata":
                case "forms":
                case "annotations":
                case "watermarks":
                {
                    RejectSettings(optionsPath);
                    options.Complete();
                    var document = ReadPdf(input);
                    SaveJson(command switch
                    {
                        "metadata" => PdfMetadataExtractor.Extract(document),
                        "forms" => PdfFormExtractor.Extract(document),
                        "annotations" => PdfAnnotationExtractor.Extract(document),
                        "watermarks" => PdfWatermarkEditor.GetWatermarks(document),
                        _ => DocumentInspection.Inspect(document, cancellation)
                    });
                    break;
                }
                case "search":
                {
                    var query = options.Require("query");
                    var settings = Settings<PdfTextSearchOptions>();
                    options.Complete();
                    SaveJson(PdfTextSearcher.Search(ReadPdf(input), query, settings, cancellation));
                    break;
                }
                case "render-data":
                case "image-data":
                {
                    RejectSettings(optionsPath);
                    var page = options.Integer("page", 1);
                    var image = command == "image-data" ? options.Integer("image") : 0;
                    options.Complete();
                    var document = ReadPdf(input);
                    SaveJson(command == "image-data" ? PdfCanvasExporter.ExportImage(document, page, image) : PdfCanvasExporter.Export(document, page));
                    break;
                }
                case "edit":
                {
                    var edit = RequiredSettings<EditRequest>();
                    edit = edit with { Value = edit.Value ?? "" };
                    var ocrLines = options.Take("ocr-lines");
                    if (ocrLines is not null)
                    {
                        if (edit.Kind != "apply-image-ocr")
                        {
                            throw new ArgumentException("--ocr-lines requires an apply-image-ocr edit.");
                        }
                        edit = edit with { OcrLines = ReadJson<List<PdfOcrTextLine>>(files.Input(ocrLines)) };
                    }
                    var certificatePassword = options.Take("certificate-password-env");
                    if (certificatePassword is not null)
                    {
                        edit = edit with { CertificatePassword = EnvironmentValue(certificatePassword) };
                    }
                    var assets = new Dictionary<string, string?>
                    {
                        ["image"] = options.Take("image-file"),
                        ["ocrFont"] = options.Take("font-file"),
                        ["certificate"] = options.Take("certificate-file")
                    };
                    options.Complete();
                    var bytes = await DocumentEdits.Apply(ReadPdf(input), edit, (name, limit) =>
                    {
                        var path = files.Input(assets[name] ?? throw new ArgumentException($"Provide the {name} asset file."));
                        if (new FileInfo(path).Length is <= 0 || new FileInfo(path).Length > limit)
                        {
                            throw new ArgumentException($"{name} must be between 1 and {limit} bytes.");
                        }
                        return File.ReadAllBytesAsync(path, cancellation);
                    }, cancellation);
                    files.Add(destination, bytes);
                    break;
                }
                case "split":
                case "extract":
                case "reorder-pages":
                case "delete-page":
                case "duplicate-page":
                case "insert-blank-page":
                case "rotate-page":
                {
                    var settings = Settings<PdfPageAssemblyOptions>();
                    var pages = command is "extract" or "reorder-pages" ? ParsePages(options.Require("pages")) : [];
                    var page = command is "delete-page" or "duplicate-page" or "insert-blank-page" or "rotate-page" ? options.Require("page") : null;
                    var angle = command == "rotate-page" ? options.Integer("degrees") : 0;
                    options.Complete();
                    var document = ReadPdf(input);
                    EnsureUnsigned(document);
                    var pageNumber = page == "last" ? PdfImageExtractor.Extract(document).Count : page is null ? 0 : int.Parse(page, System.Globalization.CultureInfo.InvariantCulture);
                    if (command == "split")
                    {
                        var number = 0;
                        foreach (var part in PdfPageEditor.SplitPages(document, settings, cancellation))
                        {
                            files.Add(Path.Combine(destination, $"page-{++number:D4}.pdf"), PdfWriter.Write(part));
                        }
                    }
                    else
                    {
                        if (command == "rotate-page")
                        {
                            PdfPageEditor.RotatePage(document, pageNumber, angle);
                        }
                        else
                        {
                            document = command switch
                            {
                                "extract" => PdfPageEditor.ExtractPages(document, pages, settings, cancellation),
                                "reorder-pages" => PdfPageEditor.ReorderPages(document, pages, settings, cancellation),
                                "delete-page" => PdfPageEditor.DeletePage(document, pageNumber, settings, cancellation),
                                "duplicate-page" => PdfPageEditor.DuplicatePage(document, pageNumber, settings, cancellation),
                                _ => PdfPageEditor.InsertBlankPage(document, pageNumber, settings, cancellation)
                            };
                        }
                        files.Add(destination, PdfWriter.Write(document));
                    }
                    break;
                }
                case "merge":
                {
                    // Manifest paths are relative to the manifest, not the working directory.
                    var paths = ReadJson<List<string>>(input);
                    if (paths.Count < 2)
                    {
                        throw new ArgumentException("The merge manifest must contain at least two PDF paths.");
                    }
                    var settings = Settings<PdfPageAssemblyOptions>();
                    options.Complete();
                    if (optionsPath is null)
                    {
                        settings = settings with { NamespaceFormFields = true, RenameConflictingAttachments = true, RenameConflictingDestinations = true };
                    }
                    var documents = paths.Select(path => ReadPdf(Path.Combine(Path.GetDirectoryName(input)!, path)));
                    files.Add(destination, PdfWriter.Write(PdfDocumentMerger.Merge(documents, settings, cancellation)));
                    break;
                }
                case "flatten":
                {
                    var settings = Settings<PdfFlattenOptions>();
                    options.Complete();
                    files.Add(destination, PdfWriter.Write(PdfFlattenEditor.Flatten(ReadPdf(input), settings, cancellation)));
                    break;
                }
                case "compress":
                {
                    var settings = Settings<PdfCompressionOptions>();
                    options.Complete();
                    var result = PdfDocumentCompressor.Compress(File.ReadAllBytes(input), settings, cancellation);
                    files.Add(destination, result.Bytes);
                    error.WriteLine($"{result.OriginalBytes} -> {result.Bytes.Length} bytes; {result.RemovedObjects} unused objects removed; {result.DeduplicatedResources} resources deduplicated.");
                    foreach (var note in result.Notes)
                    {
                        error.WriteLine(note);
                    }
                    break;
                }
                case "redact":
                case "redaction-preview":
                {
                    var regions = RequiredSettings<List<PdfContentRedaction>>();
                    options.Complete();
                    var document = ReadPdf(input);
                    if (command == "redaction-preview")
                    {
                        SaveJson(PdfContentRedactor.Preview(document, regions, cancellation));
                    }
                    else
                    {
                        files.Add(destination, PdfWriter.Write(PdfContentRedactor.Apply(document, regions, cancellation)));
                    }
                    break;
                }
                case "unlock":
                case "protect":
                {
                    var password = options.Take("password-env");
                    var settings = command == "protect" ? RequiredSettings<PdfEncryptionOptions>() : null;
                    if (command == "unlock")
                    {
                        RejectSettings(optionsPath);
                    }
                    options.Complete();
                    var document = PdfReader.Read(File.ReadAllBytes(input), new() { Password = password is null ? null : EnvironmentValue(password) });
                    EnsureUnsigned(document);
                    if (document.Security is { OwnerAuthenticated: false } security
                        && (!security.AllowModifying || !security.AllowCopying || !security.AllowDocumentAssembly || !security.AllowAnnotations || !security.AllowFormFilling || !security.AllowHighQualityPrinting))
                    {
                        throw new NotSupportedException("Use the owner password to unlock a restricted PDF.");
                    }
                    files.Add(destination, PdfWriter.Write(document, new() { Encryption = settings }));
                    break;
                }
                case "render":
                case "export-image":
                case "rasterize":
                case "redact-raster":
                case "compare":
                case "ocr":
                case "qr":
                {
                    await VisualCommands.Run(command, input, destination, optionsPath, options, files, cancellation);
                    break;
                }
                default:
                {
                    throw new ArgumentException($"Unknown command: {command}. Use --help.");
                }
            }
            files.Save(output, cancellation);
            return 0;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Cancelled. No unfinished output was published.");
            return 130;
        }
        catch (Exception exception)
        {
            error.WriteLine($"Operation failed: {exception.Message}");
            return 1;
        }
    }

    internal static T ReadJson<T>(string path)
    {
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new ArgumentException($"Expected a non-null JSON value in {path}.");
    }

    private static string EnvironmentValue(string name)
    {
        return Environment.GetEnvironmentVariable(name) ?? throw new ArgumentException($"Environment variable {name} is not set.");
    }

    private static void RejectSettings(string? path)
    {
        if (path is not null)
        {
            throw new ArgumentException("This command does not accept --options.");
        }
    }

    private static void EnsureUnsigned(PdfObjectDocument document)
    {
        if (PdfFormExtractor.Extract(document).Any(field => field.IsSigned))
        {
            throw new NotSupportedException("This operation would invalidate the document's digital signature.");
        }
    }

    private static List<int> ParsePages(string text)
    {
        var pages = new List<int>();
        foreach (var part in text.Split(','))
        {
            var range = part.Split('-');
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0], out var first) || first < 1)
            {
                throw new ArgumentException("Pages must be positive numbers or ranges, e.g. 1,3-5.");
            }
            var last = first;
            if (range.Length == 2 && (!int.TryParse(range[1], out last) || last < first))
            {
                throw new ArgumentException("Page ranges must ascend.");
            }
            if ((long)last - first + pages.Count >= 10000)
            {
                throw new ArgumentException("Select at most 10000 pages.");
            }
            for (var page = first; page <= last; page++)
            {
                pages.Add(page);
            }
        }
        return pages;
    }

    private const string Help = """
        PdfObjectKit CLI — offline Playground document operations
        All commands: COMMAND INPUT OUTPUT [--overwrite]. No API server required.

        to-json input.pdf output.json [--objects-only]
        to-pdf input.json output.pdf
        to-markdown input.pdf output.md [--options markdown.json]
        inspect|metadata|forms|annotations|watermarks input.pdf output.json
        search input.pdf matches.json --query TEXT [--options search.json]
        edit input.pdf output.pdf --options edit.json
            [--image-file image.png] [--font-file font.ttf]
            [--ocr-lines lines.json]
            [--certificate-file signing.pfx] [--certificate-password-env NAME]
        split input.pdf output-directory [--options assembly.json]
        extract|reorder-pages input.pdf output.pdf --pages 1,3-5 [--options assembly.json]
        delete-page|duplicate-page|insert-blank-page input.pdf output.pdf --page N|last
        rotate-page input.pdf output.pdf --page N|last --degrees 90
        merge inputs.json output.pdf [--options assembly.json]
        flatten input.pdf output.pdf [--options flatten.json]
        compress input.pdf output.pdf [--options compression.json]
        unlock input.pdf output.pdf [--password-env NAME]
        protect input.pdf output.pdf --options security.json [--password-env NAME]
        redact|redaction-preview input.pdf output.pdf|preview.json --options regions.json
        render-data input.pdf page.json [--page 1]
        image-data input.pdf image.json --page 1 --image 1

        Optional Node.js helper (run npm ci in PdfObjectKit.Cli/visual first):
        render input.pdf page.png [--page 1] [--dpi 144]
        export-image input.pdf image.png --page 1 --image 1
        rasterize input.pdf output.pdf [--dpi 144]
        redact-raster input.pdf output.pdf --options regions.json [--dpi 144]
        compare a.pdf report-directory --other b.pdf
        ocr image.png lines.json [--language eng] [--data-dir tessdata]
        qr text.txt image.png
        Visual commands also accept --helper PATH/visual.mjs.

        edit.json uses the same edit kinds and fields as the Playground, including
        text, images, form fields, annotations, metadata, watermarks, signing and OCR.
        inspect exports the run identities needed for safe text edits.
        Merge manifest: JSON array of PDF paths relative to the manifest directory.
        Redaction regions: [{"pageNumber":1,"x":40,"y":60,"width":100,"height":20}].
        Options are strict JSON; unknown options fail instead of being ignored.
        Existing outputs require --overwrite; inputs are never overwritten.
        See PdfObjectKit.Cli/README.md for complete examples and limitations.
        """;
}
