using PuppeteerSharp;

var httpClient = new HttpClient();
var response = await httpClient.GetAsync("https://github.com/phongnguyend");

var html = await response.Content.ReadAsStringAsync();

var browserFetcher = new BrowserFetcher();
await browserFetcher.DownloadAsync();

await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true });
await using var page = await browser.NewPageAsync();
await page.SetContentAsync(html);

var data = await page.PdfDataAsync(new PdfOptions
{
    PrintBackground = true,
});

File.WriteAllBytes("abc.pdf", data);