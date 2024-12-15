using System.Diagnostics;


var filePath = "C:\\Users\\Phong.NguyenDoan\\Downloads\\abc.docx";

var tempFolder = @$"C:\Users\Phong.NguyenDoan\Downloads\Temp\{Guid.NewGuid()}";

var libreOfficePath = @"C:\Program Files\LibreOffice\program\soffice";

try
{
    using var process = new Process();
    process.StartInfo.FileName = libreOfficePath;
    process.StartInfo.Arguments = $"--convert-to pdf --outdir \"{tempFolder}\" \"{filePath}\"";
    process.StartInfo.UseShellExecute = false;
    process.StartInfo.RedirectStandardOutput = true;
    process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
    process.StartInfo.CreateNoWindow = true;
    process.Start();
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();

    File.WriteAllBytes("abc.pdf", File.ReadAllBytes(Path.Combine(tempFolder, "abc.pdf")));
}
finally
{
    if (Directory.Exists(tempFolder))
    {
        Directory.Delete(tempFolder, true);
    };
}
