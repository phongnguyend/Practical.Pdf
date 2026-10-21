using PdfObjectKit.Options;
using PdfObjectKit.Diagnostics;
using PdfObjectKit.Models;

namespace PdfObjectKit.Analysis;

/// <summary>Orders separated horizontal regions without changing source lines or table membership.</summary>
internal static class PdfLayoutAnalyzer
{
    internal sealed record Result(List<PdfParagraphLine> Lines, HashSet<PdfParagraphLine> RegionStarts,
        HashSet<PdfParagraphLine> AmbiguousLines, List<PdfDiagnostic> Diagnostics);

    public static Result Analyze(List<PdfParagraphLine> lines, HashSet<PdfParagraphLine> tableLines, int pageNumber, PdfHeuristicOptions? heuristics = null)
    {
        var h = heuristics ?? new();
        h.Validate();
        var ordered = new List<PdfParagraphLine>();
        var starts = new HashSet<PdfParagraphLine>();
        var ambiguous = new HashSet<PdfParagraphLine>();
        var diagnostics = new List<PdfDiagnostic>();
        var pending = new List<PdfParagraphLine>();
        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }

            // Large vertical whitespace separates independent regions before column analysis.
            var bands = new List<List<PdfParagraphLine>>();
            foreach (var line in pending.OrderByDescending(l => l.Baseline).ThenBy(l => l.Left))
            {
                if (bands.Count == 0 || bands[^1][^1].Baseline - line.Baseline
                    > Math.Max(bands[^1][^1].Height, line.Height) * h.RegionGapFactor)
                {
                    bands.Add([]);
                }

                bands[^1].Add(line);
            }

            foreach (var band in bands)
            {
                OrderRegion(band, 0);
            }

            pending.Clear();
        }

        void OrderRegion(List<PdfParagraphLine> region, int depth)
        {
            var height = region.Select(l => l.Height).Order().ElementAt(region.Count / 2);
            var byX = region.OrderBy(l => l.Left).ToList();
            var right = byX[0].Right;
            var gaps = new List<(double Start, double End)>();
            foreach (var line in byX.Skip(1))
            {
                if (line.Left - right > height * h.ColumnGutterFactor)
                {
                    gaps.Add((right, line.Left));
                }

                right = Math.Max(right, line.Right);
            }

            if (depth < 16)
            {
                foreach (var gap in gaps.OrderByDescending(g => g.End - g.Start))
                {
                    var split = (gap.Start + gap.End) / 2;
                    var left = region.Where(l => l.Right < split).ToList();
                    var rightColumn = region.Where(l => l.Left > split).ToList();
                    if (left.Count < h.MinimumColumnLines || rightColumn.Count < h.MinimumColumnLines || left.Count + rightColumn.Count != region.Count)
                    {
                        continue;
                    }

                    var overlap = Math.Min(left.Max(l => l.Baseline), rightColumn.Max(l => l.Baseline))
                        - Math.Max(left.Min(l => l.Baseline), rightColumn.Min(l => l.Baseline));
                    if (overlap < height * h.ColumnOverlapFactor)
                    {
                        continue;
                    }

                    OrderRegion(left, depth + 1);
                    OrderRegion(rightColumn, depth + 1);
                    return;
                }
            }

            var sorted = region.OrderByDescending(l => l.Baseline).ThenBy(l => l.Left).ToList();
            var parallel = sorted.Zip(sorted.Skip(1)).Where(pair => Math.Abs(pair.First.Baseline - pair.Second.Baseline)
                < Math.Min(pair.First.Height, pair.Second.Height) * h.AmbiguousBaselineTolerance).ToList();
            if (parallel.Count > 0)
            {
                foreach (var pair in parallel)
                {
                    ambiguous.Add(pair.First);
                    ambiguous.Add(pair.Second);
                }

                var preview = string.Join(" / ", sorted.Take(2).Select(l => l.Text.Trim()));
                preview = preview.Length > 100 ? preview[..100] + "..." : preview;
                diagnostics.Add(new PdfDiagnostic
                {
                    Code = PdfDiagnosticCode.AmbiguousReadingOrder,
                    Severity = PdfDiagnosticSeverity.Warning,
                    PageNumber = pageNumber,
                    Location = new(region.Min(l => l.Left), region.Max(l => l.Baseline)),
                    SourceObjects = region.SelectMany(l => l.Runs).Select(r => r.StreamObject).Distinct().ToArray(),
                    Message = FormattableString.Invariant($"Page {pageNumber}: ambiguous layout near x={region.Min(l => l.Left):0.##}, y={region.Max(l => l.Baseline):0.##} ({preview}); text preserved in top-to-bottom, left-to-right order. Review reading order.")
                });
            }

            starts.Add(sorted[0]);
            ordered.AddRange(sorted);
        }

        foreach (var line in lines)
        {
            if (tableLines.Contains(line))
            {
                Flush();
                ordered.Add(line);
            }
            else
            {
                pending.Add(line);
            }
        }

        Flush();
        return new(ordered, starts, ambiguous, diagnostics);
    }
}
