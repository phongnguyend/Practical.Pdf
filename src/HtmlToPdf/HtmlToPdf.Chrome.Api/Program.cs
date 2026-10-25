using System.Diagnostics;

const long MaxHtmlFileSize = 10 * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = MaxHtmlFileSize + (1024 * 1024));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/convert", async (IFormFile file, CancellationToken cancellationToken) =>
{
    if (file.Length == 0)
    {
        return Results.BadRequest("The uploaded HTML file is empty.");
    }

    if (file.Length > MaxHtmlFileSize)
    {
        return Results.BadRequest("The uploaded HTML file exceeds the 10 MB limit.");
    }

    if (!string.Equals(Path.GetExtension(file.FileName), ".html", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Path.GetExtension(file.FileName), ".htm", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Upload a file with an .html or .htm extension.");
    }

    var tempHtmlPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.html");
    var tempPdfPath = Path.ChangeExtension(tempHtmlPath, ".pdf");

    try
    {
        await using (var stream = File.Create(tempHtmlPath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var chromePath = Environment.GetEnvironmentVariable("CHROME_BIN")
            ?? (OperatingSystem.IsWindows()
                ? @"C:\Program Files\Google\Chrome\Application\chrome.exe"
                : "/usr/bin/google-chrome");

        using var process = new Process();
        process.StartInfo.FileName = chromePath;
        process.StartInfo.ArgumentList.Add("--headless");
        process.StartInfo.ArgumentList.Add("--disable-gpu");
        process.StartInfo.ArgumentList.Add("--disable-dev-shm-usage");
        if (!OperatingSystem.IsWindows())
        {
            process.StartInfo.ArgumentList.Add("--no-sandbox");
        }

        process.StartInfo.ArgumentList.Add($"--print-to-pdf={tempPdfPath}");
        process.StartInfo.ArgumentList.Add("--no-pdf-header-footer");
        process.StartInfo.ArgumentList.Add(new Uri(tempHtmlPath).AbsoluteUri);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0 || !File.Exists(tempPdfPath))
        {
            var error = await standardError;
            var output = await standardOutput;
            return Results.Problem(
                $"Chrome failed to create the PDF. Exit code: {process.ExitCode}. {error}{output}");
        }

        var pdf = await File.ReadAllBytesAsync(tempPdfPath, cancellationToken);
        var downloadName = $"{Path.GetFileNameWithoutExtension(file.FileName)}.pdf";
        return Results.File(pdf, "application/pdf", downloadName);
    }
    finally
    {
        File.Delete(tempHtmlPath);
        File.Delete(tempPdfPath);
    }
})
.DisableAntiforgery()
.WithName("ConvertHtmlToPdf");

app.Run();
