using Microsoft.Playwright;

var httpClient = new HttpClient();
var response = await httpClient.GetAsync("https://github.com/phongnguyend");
var html = await response.Content.ReadAsStringAsync();
var outputPath = args.Length > 0 ? args[0] : "abc.pdf";

using var playwright = await Playwright.CreateAsync();

await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = true,
    Args = ["--no-sandbox"],
});

var page = await browser.NewPageAsync();
await page.SetContentAsync(html);

var data = await page.PdfAsync(new PagePdfOptions
{
    Format = "A4",
});

await File.WriteAllBytesAsync(outputPath, data);
