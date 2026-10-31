namespace PdfObjectKit.Cli;

internal sealed class OutputFiles(bool overwrite)
{
    private static readonly StringComparer Comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly HashSet<string> inputs = new(Comparer);
    private readonly Dictionary<string, byte[]> outputs = new(Comparer);

    public string Input(string path)
    {
        path = Path.GetFullPath(path);
        inputs.Add(path);
        return path;
    }

    public void Add(string path, byte[] bytes)
    {
        if (!outputs.TryAdd(Path.GetFullPath(path), bytes))
        {
            throw new ArgumentException($"Duplicate output path: {path}");
        }
    }

    public void Save(TextWriter log, CancellationToken cancellation)
    {
        foreach (var path in outputs.Keys)
        {
            if (inputs.Contains(path))
            {
                throw new ArgumentException("Input and output must use different paths, including options and asset files.");
            }
            if (Directory.Exists(path) || (!overwrite && File.Exists(path)))
            {
                throw new IOException($"Output already exists: {path}");
            }
        }
        var staged = new List<(string Temporary, string Destination)>();
        try
        {
            foreach (var (path, bytes) in outputs)
            {
                cancellation.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".pdfobjectkit-{Guid.NewGuid():N}.tmp");
                staged.Add((temporary, path));
                using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            cancellation.ThrowIfCancellationRequested();
            foreach (var (temporary, destination) in staged)
            {
                File.Move(temporary, destination, overwrite);
                log.WriteLine($"Saved {destination}");
            }
        }
        finally
        {
            foreach (var (temporary, _) in staged)
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
