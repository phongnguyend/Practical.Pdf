using System.ComponentModel;
using System.Diagnostics;

public static class UrlConversionEndpoints
{
    public static void MapUrlConversion(this WebApplication app)
    {
        app.MapPost("/convert-url", async (UrlConversionRequest request, CancellationToken cancellationToken) =>
        {
            if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrWhiteSpace(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return Results.BadRequest("Enter an absolute HTTP or HTTPS URL without embedded credentials.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                var pdf = await RenderAsync(uri.AbsoluteUri, timeout.Token);
                if (pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
                {
                    return Results.Problem("The browser did not produce a valid PDF.", statusCode: 502);
                }

                return Results.File(pdf, "application/pdf", "webpage.pdf");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Results.Problem("URL conversion timed out.", statusCode: 504);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                app.Logger.LogWarning(exception, "URL conversion failed.");
                return Results.Problem("Could not render the URL. Check that the page is reachable from the converter.", statusCode: 502);
            }
        })
        .WithName("ConvertUrlToPdf");
    }

    private static async Task<byte[]> RenderAsync(string url, CancellationToken cancellationToken)
    {
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = OperatingSystem.IsWindows() ? [] : ["--no-sandbox"],
        });
        var page = await browser.NewPageAsync();
        var response = await page.GotoAsync(url, new Microsoft.Playwright.PageGotoOptions
        {
            WaitUntil = Microsoft.Playwright.WaitUntilState.Load,
            Timeout = 30_000,
        }).WaitAsync(cancellationToken);
        if (response is not null && !response.Ok)
        {
            throw new InvalidOperationException($"The page returned HTTP {response.Status}.");
        }

        return await page.PdfAsync(new Microsoft.Playwright.PagePdfOptions
        {
            Format = "A4",
            PrintBackground = true,
        }).WaitAsync(cancellationToken);
    }
}

public sealed record UrlConversionRequest(string? Url);

