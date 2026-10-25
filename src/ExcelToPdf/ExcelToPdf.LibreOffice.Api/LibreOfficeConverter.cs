using System.Diagnostics;
using System.IO.Compression;

namespace ExcelToPdf.LibreOffice;

public static class LibreOfficeConverter
{
    public static async Task<byte[]> ConvertAsync(
        Stream input,
        string extension,
        CancellationToken cancellationToken = default)
    {
        if (extension is not (".xlsx" or ".xls"))
        {
            throw new ArgumentException("Unsupported Office document extension.", nameof(extension));
        }

        var directory = Path.Combine(Path.GetTempPath(), "excel-to-pdf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));

        try
        {
            var inputPath = Path.Combine(directory, "document" + extension);
            await using (var stream = File.Create(inputPath))
            {
                await input.CopyToAsync(stream, timeout.Token);
            }

            if (extension == ".xlsx")
            {
                using var archive = ZipFile.OpenRead(inputPath);
                if (archive.GetEntry("xl/workbook.xml") is not { Length: > 0 })
                {
                    throw new InvalidDataException("The uploaded file is not a valid Excel document.");
                }
            }
            var profilePath = Path.Combine(directory, "profile");
            Directory.CreateDirectory(profilePath);
            // Untrusted uploads must not execute document macros.
            await File.WriteAllTextAsync(Path.Combine(profilePath, "registrymodifications.xcu"),
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <oor:items xmlns:oor="http://openoffice.org/2001/registry">
                  <item oor:path="/org.openoffice.Office.Common/Security/Scripting">
                    <prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop>
                  </item>
                </oor:items>
                """, timeout.Token);

            using var process = new Process();
            process.StartInfo.FileName = Environment.GetEnvironmentVariable("LIBREOFFICE_BIN")
                ?? (OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.com")
                    : "soffice");
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.ArgumentList.Add($"-env:UserInstallation={new Uri(profilePath).AbsoluteUri}");
            process.StartInfo.ArgumentList.Add("--headless");
            process.StartInfo.ArgumentList.Add("--norestore");
            process.StartInfo.ArgumentList.Add("--convert-to");
            process.StartInfo.ArgumentList.Add("pdf:calc_pdf_Export");
            process.StartInfo.ArgumentList.Add("--outdir");
            process.StartInfo.ArgumentList.Add(directory);
            process.StartInfo.ArgumentList.Add(inputPath);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process may have exited between cancellation and Kill.
                }

                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(output, error);
                throw;
            }

            await Task.WhenAll(output, error);
            var pdfPath = Path.Combine(directory, "document.pdf");
            if (process.ExitCode != 0 || !File.Exists(pdfPath))
            {
                throw new InvalidDataException("LibreOffice could not convert this document. Check that it is valid and not password protected.");
            }

            var pdf = await File.ReadAllBytesAsync(pdfPath, timeout.Token);
            if (pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            {
                throw new InvalidDataException("LibreOffice did not produce a valid PDF.");
            }

            return pdf;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Conversion exceeded the 90-second time limit.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
