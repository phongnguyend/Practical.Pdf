using System.Globalization;

namespace PdfObjectKit.Cli;

internal sealed class CliArguments
{
    private readonly Dictionary<string, string> options = new(StringComparer.Ordinal);

    public List<string> Paths { get; } = [];

    public CliArguments(IEnumerable<string> arguments)
    {
        var args = arguments.ToArray();
        var literal = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!literal && arg == "--")
            {
                literal = true;
            }
            else if (!literal && arg.StartsWith("--", StringComparison.Ordinal))
            {
                var flag = arg is "--overwrite" or "--objects-only";
                if (!flag && (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)))
                {
                    throw new ArgumentException($"{arg} requires a value.");
                }
                if (!options.TryAdd(arg, flag ? "true" : args[++i]))
                {
                    throw new ArgumentException($"Duplicate option: {arg}");
                }
            }
            else
            {
                Paths.Add(Path.GetFullPath(arg));
            }
        }
    }

    public string? Take(string name)
    {
        return options.Remove("--" + name, out var value) ? value : null;
    }

    public string Require(string name)
    {
        return Take(name) ?? throw new ArgumentException($"Provide --{name}.");
    }

    public int Integer(string name, int? fallback = null)
    {
        var value = Take(name);
        if (value is null && fallback.HasValue)
        {
            return fallback.Value;
        }
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            throw new ArgumentException($"Provide an integer for --{name}.");
        }
        return number;
    }

    public void Complete()
    {
        if (options.Count > 0)
        {
            throw new ArgumentException($"Unknown option: {options.Keys.First()}. Use --help.");
        }
    }
}
