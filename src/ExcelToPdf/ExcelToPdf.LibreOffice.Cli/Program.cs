using ExcelToPdf.LibreOffice;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: ExcelToPdf.LibreOffice.Cli <input.xlsx|input.xls> [output.pdf]");
    return 1;
}

try
{
    var inputPath = Path.GetFullPath(args[0]);
    var extension = Path.GetExtension(inputPath).ToLowerInvariant();
    if (extension is not (".xlsx" or ".xls"))
    {
        Console.Error.WriteLine("Choose a .xlsx or .xls file.");
        return 1;
    }

    var outputPath = args.Length == 2 ? Path.GetFullPath(args[1]) : Path.ChangeExtension(inputPath, ".pdf");
    if (!string.Equals(Path.GetExtension(outputPath), ".pdf", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("The output path must have a .pdf extension.");
        return 1;
    }

    await using var input = File.OpenRead(inputPath);
    if (input.Length is 0 or > 10 * 1024 * 1024)
    {
        Console.Error.WriteLine("Choose a non-empty file no larger than 10 MB.");
        return 1;
    }

    var pdf = await LibreOfficeConverter.ConvertAsync(input, extension);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    await File.WriteAllBytesAsync(outputPath, pdf);
    Console.WriteLine(outputPath);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
