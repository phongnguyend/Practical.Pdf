using Microsoft.Playwright;

var httpClient = new HttpClient();
var response = await httpClient.GetAsync("https://github.com/phongnguyend");

var html = await response.Content.ReadAsStringAsync();

Microsoft.Playwright.Program.Main(["install"]);

using var playwright = await Playwright.CreateAsync();

var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = true,
});

var page = await browser.NewPageAsync();
await page.SetContentAsync(html);

var data = await page.PdfAsync(new PagePdfOptions
{
    Format = "A4",
});

File.WriteAllBytes("abc.pdf", data);