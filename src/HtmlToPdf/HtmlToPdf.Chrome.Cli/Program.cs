using System.Diagnostics;

var tempFolder = Path.GetTempPath();
var guid = Guid.NewGuid();
var tempHtml = Path.Combine(tempFolder, $"{guid}.html");
var tempResult = Path.Combine(tempFolder, $"{guid}.pdf");
var outputPath = args.Length > 0 ? args[0] : "abc.pdf";

var chromePath = Environment.GetEnvironmentVariable("CHROME_BIN")
    ?? (OperatingSystem.IsWindows()
        ? @"C:\Program Files\Google\Chrome\Application\chrome.exe"
        : "/usr/bin/google-chrome");

var httpClient = new HttpClient();
var response = await httpClient.GetAsync("https://github.com/phongnguyend");
var html = await response.Content.ReadAsStringAsync();

try
{
    File.WriteAllText(tempHtml, html);

    using var process = new Process();
    process.StartInfo.FileName = chromePath;
    process.StartInfo.ArgumentList.Add("--headless");
    process.StartInfo.ArgumentList.Add("--disable-gpu");
    process.StartInfo.ArgumentList.Add("--disable-dev-shm-usage");
    process.StartInfo.ArgumentList.Add("--no-sandbox");
    process.StartInfo.ArgumentList.Add($"--print-to-pdf={tempResult}");
    process.StartInfo.ArgumentList.Add("--no-pdf-header-footer");
    process.StartInfo.ArgumentList.Add(new Uri(tempHtml).AbsoluteUri);
    process.StartInfo.UseShellExecute = false;
    process.StartInfo.RedirectStandardOutput = true;
    process.StartInfo.RedirectStandardError = true;
    process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
    process.StartInfo.CreateNoWindow = true;
    process.Start();

    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Chrome exited with code {process.ExitCode}: {error}{output}");
    }

    File.Copy(tempResult, outputPath, overwrite: true);
}
finally
{
    if (File.Exists(tempHtml))
    {
        File.Delete(tempHtml);
    }

    if (File.Exists(tempResult))
    {
        File.Delete(tempResult);
    }
}
