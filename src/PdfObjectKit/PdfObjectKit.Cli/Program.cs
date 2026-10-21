using PdfObjectKit;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("""
        PDF / JSON converter
        to-json input.pdf output.json [--objects-only] [--overwrite]
        to-pdf input.json output.pdf [--overwrite]

        All commands accept --overwrite to replace an existing output.
        Without it, outputs must not already exist. to-pdf rebuilds the PDF object graph.
        Text/image indexes are informational; use the C# editor APIs to change content.
        --objects-only skips text/image indexes and uses only the native parser.
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
    switch (args[0])
    {
        case "to-json":
            output = System.Text.Encoding.UTF8.GetBytes(PdfConverter.ToJson(PdfReader.Read(File.ReadAllBytes(args[1]), includeIndexes: !objectsOnly)));
            break;
        case "to-pdf":
            for (var index = 3; index < args.Length; index++)
            {
                if (args[index] == "--overwrite" && !overwrite)
                {
                    overwrite = true;
                }
                else
                {
                    throw new ArgumentException("Unknown or duplicate to-pdf option. Use --help.");
                }
            }

            output = PdfWriter.Write(PdfConverter.FromJson(File.ReadAllText(args[1])));
            break;
        default:
            throw new ArgumentException("Unknown command or options. Use --help.");
    }

    if (overwrite)
    {
        var destination = Path.GetFullPath(args[2]);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".pdfobjectkit-{Guid.NewGuid():N}.tmp");
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
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
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
