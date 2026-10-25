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
        var directory = Path.Combine(Path.GetTempPath(), "html-to-pdf-url", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pdfPath = Path.Combine(directory, "webpage.pdf");
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = Environment.GetEnvironmentVariable("CHROME_BIN")
                ?? (OperatingSystem.IsWindows()
                    ? @"C:\Program Files\Google\Chrome\Application\chrome.exe"
                    : "/usr/bin/google-chrome");
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.ArgumentList.Add("--headless");
            process.StartInfo.ArgumentList.Add("--disable-gpu");
            process.StartInfo.ArgumentList.Add("--disable-dev-shm-usage");
            if (!OperatingSystem.IsWindows())
            {
                process.StartInfo.ArgumentList.Add("--no-sandbox");
            }

            process.StartInfo.ArgumentList.Add($"--user-data-dir={Path.Combine(directory, "profile")}");
            process.StartInfo.ArgumentList.Add($"--print-to-pdf={pdfPath}");
            process.StartInfo.ArgumentList.Add("--no-pdf-header-footer");
            process.StartInfo.ArgumentList.Add("--timeout=30000");
            process.StartInfo.ArgumentList.Add(url);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Chrome exited before cancellation reached the process.
                }

                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(output, error);
                throw;
            }

            await Task.WhenAll(output, error);
            if (process.ExitCode != 0 || !File.Exists(pdfPath))
            {
                throw new InvalidOperationException("Chrome failed to create the PDF.");
            }

            return await File.ReadAllBytesAsync(pdfPath, cancellationToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed record UrlConversionRequest(string? Url);

