using System.Diagnostics;

var tempFoler = @$"C:\Users\Phong.NguyenDoan\Downloads\Temp";
var guid = Guid.NewGuid();
var tempHtml = Path.Combine(tempFoler, $"{guid}.html");
var tempResult = Path.Combine(tempFoler, $"{guid}.pdf");

var chromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

var httpClient = new HttpClient();
var response = await httpClient.GetAsync("https://github.com/phongnguyend");
var html = await response.Content.ReadAsStringAsync();

try
{
    File.WriteAllText(tempHtml, html);

    using var process = new Process();
    process.StartInfo.FileName = chromePath;
    process.StartInfo.Arguments = $"--headless --disable-gpu --print-to-pdf=\"{tempResult}\" --no-pdf-header-footer \"{tempHtml}\"";
    process.StartInfo.UseShellExecute = false;
    process.StartInfo.RedirectStandardOutput = true;
    process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
    process.StartInfo.CreateNoWindow = true;
    process.Start();
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();

    File.WriteAllBytes("abc.pdf", File.ReadAllBytes(tempResult));
}
finally
{
    if (File.Exists(tempHtml))
    {
        File.Delete(tempHtml);
    };

    if (File.Exists(tempResult))
    {
        File.Delete(tempResult);
    };
}
