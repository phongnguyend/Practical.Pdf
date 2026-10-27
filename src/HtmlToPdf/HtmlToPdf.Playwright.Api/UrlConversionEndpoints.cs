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

        try
        {
            await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle,
                new Microsoft.Playwright.PageWaitForLoadStateOptions { Timeout = 10_000 }).WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            // Analytics and long polling can keep a page busy indefinitely.
        }

        await page.EvaluateAsync("""
            async () => {
                const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
                // Allow hydration and delayed requests to start, then expose lazy content.
                await delay(2000);
                for (let step = 0; step < 40; step++) {
                    window.scrollBy(0, Math.max(window.innerHeight, 600));
                    await delay(100);
                    if (window.scrollY + window.innerHeight >= document.documentElement.scrollHeight) {
                        break;
                    }
                }
                window.scrollTo(0, 0);
                let lastChange = Date.now();
                const observer = new MutationObserver(() => {
                    lastChange = Date.now();
                });
                observer.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true });
                const started = Date.now();
                try {
                    while (Date.now() - lastChange < 1000 && Date.now() - started < 10000) {
                        await delay(100);
                    }
                } finally {
                    observer.disconnect();
                }
                for (const image of document.images) {
                    image.loading = 'eager';
                }
                await Promise.race([
                    Promise.all([
                        document.fonts.ready,
                        ...Array.from(document.images, image => image.decode().catch(() => {
                            // A broken image must not prevent the rest of the page from printing.
                        }))
                    ]),
                    delay(10000)
                ]);
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
            }
            """).WaitAsync(cancellationToken);

        return await page.PdfAsync(new Microsoft.Playwright.PagePdfOptions
        {
            Format = "A4",
            PrintBackground = true,
        }).WaitAsync(cancellationToken);
    }
}

public sealed record UrlConversionRequest(string? Url);

