using Practical.PdfJson;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("""
        PDF / JSON converter
        to-json input.pdf output.json [--objects-only] [--overwrite]
        to-pdf input.json output.pdf [--font path/to/font.ttf] [--overwrite]
        text-to-json input.pdf output.json [--include-original] [--overwrite]
        text-to-pdf input.json output.pdf [--font path/to/font.ttf] [--overwrite]
        restore input.json output.pdf [--overwrite]

        All commands accept --overwrite to replace an existing output.
        Without it, outputs must not already exist. to-pdf rebuilds the PDF object graph.
        Text edits keep font size and may overlap. Text is never automatically shrunk.
        --objects-only skips text/image indexes and uses only the native parser.
        text-* commands use the older, text-only format.
        restore writes the embedded original exactly and ignores text edits.
        """);
    return 0;
}

try
{
    if (args.Length < 3) throw new ArgumentException("Expected command, input path and output path. Use --help.");
    var overwrite = false;
    var includeOriginal = false;
    var objectsOnly = false;
    if (string.Equals(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        throw new ArgumentException("Input and output must use different paths.");
    if (args[0] is "to-json" or "text-to-json")
    {
        foreach (var option in args.Skip(3))
        {
            if (option == "--overwrite" && !overwrite) overwrite = true;
            else if (option == "--objects-only" && args[0] == "to-json" && !objectsOnly) objectsOnly = true;
            else if (option == "--include-original" && args[0] == "text-to-json" && !includeOriginal) includeOriginal = true;
            else throw new ArgumentException("Unknown or duplicate JSON export option. Use --help.");
        }
    }
    byte[] output;
    switch (args[0])
    {
        case "to-json":
            output = System.Text.Encoding.UTF8.GetBytes(PdfObjectConverter.ToJson(
                PdfObjectConverter.ReadPdf(File.ReadAllBytes(args[1]), includeIndexes: !objectsOnly)));
            break;
        case "to-pdf":
            byte[]? fallbackFont = null;
            for (var index = 3; index < args.Length; index++)
            {
                if (args[index] == "--overwrite" && !overwrite) overwrite = true;
                else if (args[index] == "--font" && fallbackFont is null && index + 1 < args.Length)
                    fallbackFont = File.ReadAllBytes(args[++index]);
                else throw new ArgumentException("Unknown or duplicate to-pdf option. Use --help.");
            }
            output = PdfObjectConverter.WritePdf(PdfObjectConverter.FromJson(File.ReadAllText(args[1])),
                new() { FallbackFont = fallbackFont });
            break;
        case "text-to-json":
            var document = PdfJsonConverter.ReadPdf(File.ReadAllBytes(args[1]), includeOriginal);
            output = System.Text.Encoding.UTF8.GetBytes(PdfJsonConverter.ToJson(document));
            if (document.Pages.Any(p => p.Text.Count == 0))
                Console.Error.WriteLine("Note: some pages contain no extracted text. OCR is not included.");
            break;
        case "text-to-pdf":
            byte[]? textFont = null;
            for (var index = 3; index < args.Length; index++)
            {
                if (args[index] == "--overwrite" && !overwrite) overwrite = true;
                else if (args[index] == "--font" && textFont is null && index + 1 < args.Length)
                    textFont = File.ReadAllBytes(args[++index]);
                else throw new ArgumentException("Unknown or duplicate text-to-pdf option. Use --help.");
            }
            output = PdfJsonConverter.WritePdf(
                PdfJsonConverter.FromJson(File.ReadAllText(args[1])),
                textFont);
            break;
        case "restore" when args.Length == 3 || (args.Length == 4 && args[3] == "--overwrite"):
            overwrite = args.Length == 4;
            output = PdfJsonConverter.RestoreOriginal(PdfJsonConverter.FromJson(File.ReadAllText(args[1])));
            break;
        default:
            throw new ArgumentException("Unknown command or options. Use --help.");
    }

    if (overwrite)
    {
        var destination = Path.GetFullPath(args[2]);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".pdfjson-{Guid.NewGuid():N}.tmp");
        try
        {
            // Finish writing in the destination directory before replacing the existing file.
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(output);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
    else
    {
        using var stream = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write);
        stream.Write(output);
    }
    Console.WriteLine($"Saved {Path.GetFullPath(args[2])}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Conversion failed: {exception.Message}");
    return 1;
}
