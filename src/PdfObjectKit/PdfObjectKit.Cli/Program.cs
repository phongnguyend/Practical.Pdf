using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using PdfObjectKit.Converters;
using PdfObjectKit;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("""
        PDF / JSON converter
        to-json input.pdf output.json [--objects-only] [--overwrite]
        to-pdf input.json output.pdf [--overwrite]
        to-markdown input.pdf output.md [--overwrite]

        All commands accept --overwrite to replace an existing output.
        Without it, outputs must not already exist. to-pdf rebuilds the PDF object graph.
        Text/image indexes are informational; use the C# editor APIs to change content.
        to-json also writes <output>.texts.json, <output>.images.json and <output>.pagaraphs.json.
        --objects-only skips all three derived files.
        """);
    return 0;
}

try
{
    if (args.Length < 3)
    {
        throw new ArgumentException("Expected command, input path and output path. Use --help.");
    }

    var overwrite = false;
    var objectsOnly = false;
    if (string.Equals(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
    {
        throw new ArgumentException("Input and output must use different paths.");
    }

    if (args[0] == "to-json")
    {
        foreach (var option in args.Skip(3))
        {
            if (option == "--overwrite" && !overwrite)
            {
                overwrite = true;
            }
            else if (option == "--objects-only" && args[0] == "to-json" && !objectsOnly)
            {
                objectsOnly = true;
            }
            else
            {
                throw new ArgumentException("Unknown or duplicate JSON export option. Use --help.");
            }
        }
    }

    byte[] output;
    var derivedOutputs = new List<(string Extension, string Json)>();
    switch (args[0])
    {
        case "to-json":
            var document = PdfReader.Read(File.ReadAllBytes(args[1]));
            output = System.Text.Encoding.UTF8.GetBytes(PdfConverter.ToJson(document));
            if (!objectsOnly)
            {
                derivedOutputs.Add((".texts.json", PdfConverter.ToTextsJson(document)));
                derivedOutputs.Add((".images.json", PdfConverter.ToImagesJson(document)));
                derivedOutputs.Add((".pagaraphs.json", PdfConverter.ToParagraphsJson(document)));
            }
            break;
        case "to-pdf":
        case "to-markdown":
            for (var index = 3; index < args.Length; index++)
            {
                if (args[index] == "--overwrite" && !overwrite)
                {
                    overwrite = true;
                }
                else
                {
                    throw new ArgumentException("Unknown or duplicate output option. Use --help.");
                }
            }

            if (args[0] == "to-markdown")
            {
                var markdown = PdfMarkdownConverter.Convert(PdfReader.Read(File.ReadAllBytes(args[1])));
                output = System.Text.Encoding.UTF8.GetBytes(markdown.Markdown);
                foreach (var diagnostic in markdown.Diagnostics)
                {
                    Console.Error.WriteLine($"{diagnostic.Severity} [{diagnostic.Code}]: {diagnostic.Message}");
                }
            }
            else
            {
                output = PdfWriter.Write(PdfConverter.FromJson(File.ReadAllText(args[1])));
            }
            break;
        default:
            throw new ArgumentException("Unknown command or options. Use --help.");
    }

    var outputs = new List<(string Path, byte[] Data)> { (Path.GetFullPath(args[2]), output) };
    foreach (var derived in derivedOutputs)
    {
        outputs.Add((Path.ChangeExtension(Path.GetFullPath(args[2]), derived.Extension), System.Text.Encoding.UTF8.GetBytes(derived.Json)));
    }

    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    foreach (var item in outputs)
    {
        if (string.Equals(item.Path, Path.GetFullPath(args[1]), comparison))
        {
            throw new ArgumentException("Input and output must use different paths.");
        }

        if (Directory.Exists(item.Path) || (!overwrite && File.Exists(item.Path)))
        {
            throw new IOException($"Output already exists: {item.Path}");
        }
    }

    var staged = new List<(string Temporary, string Destination)>();
    try
    {
        foreach (var item in outputs)
        {
            var temporary = Path.Combine(Path.GetDirectoryName(item.Path)!, $".pdfobjectkit-{Guid.NewGuid():N}.tmp");
            staged.Add((temporary, item.Path));
            using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
            stream.Write(item.Data);
            stream.Flush(flushToDisk: true);
        }

        foreach (var item in staged)
        {
            File.Move(item.Temporary, item.Destination, overwrite);
            Console.WriteLine($"Saved {item.Destination}");
        }
    }
    finally
    {
        foreach (var item in staged)
        {
            if (File.Exists(item.Temporary))
            {
                File.Delete(item.Temporary);
            }
        }
    }

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Conversion failed: {exception.Message}");
    return 1;
}
