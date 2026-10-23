using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Rendering;

internal static class PdfCanvasGradient
{
    public static PdfCanvasCommand Create(PdfGraph graph, PdfValue pattern, bool stroke)
    {
        var shading = graph.Get(pattern, "Shading") ?? throw new NotSupportedException("Missing pattern shading.");
        if (graph.Number(pattern, "PatternType") != 2 || graph.Number(shading, "ShadingType") != 2 || graph.Get(shading, "ColorSpace")?.Name != "DeviceRGB")
        {
            throw new NotSupportedException("Only axial DeviceRGB shading patterns are supported.");
        }
        var extend = graph.Get(shading, "Extend")?.Items;
        if (extend is not { Count: 2 } || !extend.All(item => graph.Resolve(item).Boolean == true) || graph.Get(pattern, "ExtGState") is not null)
        {
            throw new NotSupportedException("Nonextended gradients and pattern graphics states are not supported.");
        }
        double[] Values(PdfValue value, string key, double[] fallback)
        {
            return graph.Get(value, key)?.Items?.Select(item => Number(graph.Resolve(item))).ToArray() ?? fallback;
        }
        double[] Evaluate(PdfValue function, double input, int depth)
        {
            if (depth > 20)
            {
                throw new NotSupportedException("Gradient function nesting exceeds limit.");
            }
            var domain = Values(function, "Domain", [0, 1]);
            if (domain.Length != 2 || domain[0] >= domain[1])
            {
                throw new InvalidDataException("Invalid gradient function domain.");
            }
            input = Math.Clamp(input, domain[0], domain[1]);
            double[] result;
            if (graph.Number(function, "FunctionType") == 2)
            {
                var start = Values(function, "C0", [0]);
                var end = Values(function, "C1", [1]);
                if (start.Length != end.Length)
                {
                    throw new InvalidDataException("Mismatched gradient components.");
                }
                var fraction = Math.Pow(input, graph.Number(function, "N", 1));
                result = start.Select((component, i) => component + fraction * (end[i] - component)).ToArray();
            }
            else if (graph.Number(function, "FunctionType") == 3)
            {
                var functions = graph.Get(function, "Functions")?.Items ?? throw new InvalidDataException("Missing gradient functions.");
                var bounds = Values(function, "Bounds", []);
                var encode = Values(function, "Encode", []);
                if (functions.Count == 0 || bounds.Length != functions.Count - 1 || encode.Length != functions.Count * 2)
                {
                    throw new InvalidDataException("Invalid stitching function.");
                }
                var edges = new[] { domain[0] }.Concat(bounds).Append(domain[1]).ToArray();
                if (edges.Zip(edges.Skip(1)).Any(pair => pair.First >= pair.Second))
                {
                    throw new InvalidDataException("Invalid gradient bounds.");
                }
                var index = 0;
                while (index < bounds.Length && input >= bounds[index])
                {
                    index++;
                }
                var fraction = (input - edges[index]) / (edges[index + 1] - edges[index]);
                result = Evaluate(graph.Resolve(functions[index]), encode[index * 2] + fraction * (encode[index * 2 + 1] - encode[index * 2]), depth + 1);
            }
            else
            {
                throw new NotSupportedException("Only exponential and stitching gradient functions are supported.");
            }
            var range = Values(function, "Range", []);
            if (range.Length != 0)
            {
                if (range.Length != result.Length * 2)
                {
                    throw new InvalidDataException("Invalid gradient range.");
                }
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] = Math.Clamp(result[i], range[i * 2], range[i * 2 + 1]);
                }
            }
            return result;
        }
        var coords = Values(shading, "Coords", []);
        var matrix = Values(pattern, "Matrix", [1, 0, 0, 1, 0, 0]);
        var domain = Values(shading, "Domain", [0, 1]);
        if (coords.Length != 4 || matrix.Length != 6 || domain.Length != 2 || domain[0] >= domain[1])
        {
            throw new InvalidDataException("Invalid gradient geometry.");
        }
        var function = graph.Get(shading, "Function") ?? throw new InvalidDataException("Missing gradient function.");
        var stops = new List<double>();
        for (var i = 0; i <= 256; i++)
        {
            var fraction = i / 256d;
            var input = domain[0] + fraction * (domain[1] - domain[0]);
            var rgb = function.Items is { } functions
                ? functions.SelectMany(item => Evaluate(graph.Resolve(item), input, 0)).ToArray()
                : Evaluate(function, input, 0);
            if (rgb.Length != 3 || rgb.Any(value => !double.IsFinite(value)))
            {
                throw new NotSupportedException("Gradient must produce three finite RGB components.");
            }
            stops.Add(fraction);
            stops.AddRange(rgb.Select(value => Math.Clamp(value, 0, 1)));
        }
        return new(stroke ? "strokeGradient" : "fillGradient") { Numbers = [.. coords, .. matrix, .. stops] };
    }
}
