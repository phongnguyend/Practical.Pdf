using PuppeteerSharp;

var httpClient = new HttpClient();
var response = await httpClient.GetAsync("https://github.com/phongnguyend");
var html = await response.Content.ReadAsStringAsync();
var outputPath = args.Length > 0 ? args[0] : "abc.pdf";

var chromePath = Environment.GetEnvironmentVariable("CHROME_BIN")
    ?? (OperatingSystem.IsWindows()
        ? @"C:\Program Files\Google\Chrome\Application\chrome.exe"
        : "/usr/bin/google-chrome");

await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
{
    Headless = true,
    ExecutablePath = chromePath,
    Args = ["--no-sandbox", "--disable-dev-shm-usage"],
});

await using var page = await browser.NewPageAsync();
await page.SetContentAsync(html);

var data = await page.PdfDataAsync(new PdfOptions
{
    PrintBackground = true,
});

await File.WriteAllBytesAsync(outputPath, data);
