using PuppeteerSharp;
using PuppeteerSharp.Media;

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

    using var reader = new StreamReader(file.OpenReadStream());
    var html = await reader.ReadToEndAsync(cancellationToken);

    var chromePath = Environment.GetEnvironmentVariable("CHROME_BIN")
        ?? (OperatingSystem.IsWindows()
            ? @"C:\Program Files\Google\Chrome\Application\chrome.exe"
            : "/usr/bin/google-chrome");

    await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
    {
        Headless = true,
        ExecutablePath = chromePath,
        Args = OperatingSystem.IsWindows()
            ? []
            : ["--no-sandbox", "--disable-dev-shm-usage"],
    });

    await using var page = await browser.NewPageAsync();
    await page.SetContentAsync(html, new SetContentOptions
    {
        WaitUntil = [WaitUntilNavigation.Networkidle0],
        Timeout = 30_000,
        CancellationToken = cancellationToken,
    });

    var pdf = await page.PdfDataAsync(new PdfOptions
    {
        Format = PaperFormat.A4,
        PrintBackground = true,
    });

    var downloadName = $"{Path.GetFileNameWithoutExtension(file.FileName)}.pdf";
    return Results.File(pdf, "application/pdf", downloadName);
})
.DisableAntiforgery()
.WithName("ConvertHtmlToPdf");

app.Run();
