using System.Diagnostics;
using System.Text.Json;
using PdfObjectKit.Editors;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Rendering;
using PdfObjectKit.Writers;

namespace PdfObjectKit.Cli;

internal static class VisualCommands
{
    public static async Task Run(string command, string input, string destination, string? settings,
        CliArguments options, OutputFiles files, CancellationToken cancellation)
    {
        var helper = Path.GetFullPath(options.Take("helper") ?? FindHelper());
        var page = command is "render" or "export-image" ? options.Integer("page", 1) : 1;
        var image = command == "export-image" ? options.Integer("image") : 0;
        var dpi = command is "render" or "rasterize" or "redact-raster" ? options.Integer("dpi", 144) : 144;
        var other = command == "compare" ? files.Input(options.Require("other")) : null;
        var language = command == "ocr" ? options.Take("language") ?? "eng" : "eng";
        var dataDirectory = command == "ocr" ? options.Take("data-dir") : null;
        if (settings is not null && command != "redact-raster")
        {
            throw new ArgumentException("Only redact-raster accepts --options among visual commands.");
        }
        var regions = command == "redact-raster"
            ? CliApplication.ReadJson<List<PdfContentRedaction>>(files.Input(settings ?? throw new ArgumentException("Provide --options regions.json.")))
            : [];
        if (command == "redact-raster" && regions.Count == 0)
        {
            throw new ArgumentException("Provide at least one redaction region.");
        }
        options.Complete();
        if (dpi is < 36 or > 600 || page < 1)
        {
            throw new ArgumentException("Use a positive page number and DPI between 36 and 600.");
        }
        if (!File.Exists(helper))
        {
            throw new FileNotFoundException("Install the optional visual helper, or specify --helper PATH/visual.mjs.");
        }
        PdfCanvasCommand? imageData = null;
        if (command is not ("ocr" or "qr"))
        {
            var document = PdfReader.Read(await File.ReadAllBytesAsync(input, cancellation));
            if (document.Security is not null)
            {
                throw new NotSupportedException("Unlock the PDF before visual operations.");
            }
            if (command == "export-image")
            {
                imageData = PdfCanvasExporter.ExportImage(document, page, image);
            }
            if (other is not null && PdfReader.Read(await File.ReadAllBytesAsync(other, cancellation)).Security is not null)
            {
                throw new NotSupportedException("Unlock both PDFs before comparison.");
            }
        }
        var temporary = Path.Combine(Path.GetTempPath(), "pdfobjectkit-visual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var requestPath = Path.Combine(temporary, "request.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
            {
                command, input, other, page, dpi, language,
                dataDirectory = dataDirectory is null ? null : Path.GetFullPath(dataDirectory),
                regions, imageData
            }, CliApplication.Json), cancellation);
            var start = new ProcessStartInfo("node")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = temporary
            };
            start.ArgumentList.Add(helper);
            start.ArgumentList.Add(requestPath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Node.js.");
            var stderr = process.StandardError.ReadToEndAsync(cancellation);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                throw;
            }
            await stdout;
            var message = await stderr;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Visual helper failed. Install Node.js 22+ and run npm ci in its visual directory. {message}");
            }
            if (command is "rasterize" or "redact-raster")
            {
                var manifest = CliApplication.ReadJson<List<RasterPage>>(Path.Combine(temporary, "pages.json"));
                var pages = new List<PdfRedactionRasterPage>();
                for (var i = 0; i < manifest.Count; i++)
                {
                    var spec = manifest[i];
                    pages.Add(new(spec.PixelWidth, spec.PixelHeight, spec.WidthPoints, spec.HeightPoints,
                        await File.ReadAllBytesAsync(Path.Combine(temporary, $"page-{i + 1}.rgb"), cancellation), spec.Regions));
                }
                files.Add(destination, PdfWriter.Write(PdfRasterRedactor.CreateDocument(pages, cancellation)));
            }
            else if (command == "compare")
            {
                foreach (var path in Directory.EnumerateFiles(temporary).Where(path => Path.GetExtension(path) is ".png" or ".html" || Path.GetFileName(path) == "comparison.json"))
                {
                    files.Add(Path.Combine(destination, Path.GetFileName(path)), await File.ReadAllBytesAsync(path, cancellation));
                }
            }
            else
            {
                files.Add(destination, await File.ReadAllBytesAsync(Path.Combine(temporary, command == "ocr" ? "result.json" : "result.png"), cancellation));
            }
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private sealed record RasterPage(int PixelWidth, int PixelHeight, double WidthPoints, double HeightPoints, List<PdfRasterRedaction> Regions);

    private static string FindHelper()
    {
        // Published installations use the adjacent helper. During dotnet run, use
        // the source helper's installed packages without copying node_modules.
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(parent.FullName, "visual"), Path.Combine(parent.FullName, "PdfObjectKit.Cli", "visual") })
            {
                if (File.Exists(Path.Combine(candidate, "visual.mjs")) && Directory.Exists(Path.Combine(candidate, "node_modules")))
                {
                    return Path.Combine(candidate, "visual.mjs");
                }
            }
        }
        return Path.Combine(AppContext.BaseDirectory, "visual", "visual.mjs");
    }
}
