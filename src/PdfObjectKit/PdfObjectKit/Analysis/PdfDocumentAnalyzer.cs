using PdfObjectKit.Options;
using PdfObjectKit.Diagnostics;
using System.Text.RegularExpressions;
using PdfObjectKit.Converters;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
using PdfObjectKit.Validators;

namespace PdfObjectKit.Analysis;

/// <summary>Heuristic horizontal text analysis with a spatial-order fallback for ambiguous layouts.</summary>
public static class PdfDocumentAnalyzer
{
    private static readonly Regex ListMarker = new(@"^\s*(?:(?<number>[0-9]{1,9})[.)]|[-*+\u2022])\s+(?<body>.+)$", RegexOptions.CultureInvariant);

    public static PdfStructuredDocument Analyze(PdfObjectDocument document, PdfMarkdownOptions? options = null)
    {
        PdfDocumentValidator.Validate(document);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.Heuristics);
        var h = options.Heuristics;
        h.Validate();
        if (options.BodyTextHeight is { } specified && (!double.IsFinite(specified) || specified <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Body text height must be finite and positive.");
        }

        var result = new PdfStructuredDocument();
        var graph = new PdfGraph(document);
        var pageLines = new List<(PdfContentPage Page, List<PdfParagraphLine> Lines)>();
        var ambiguousLines = new HashSet<PdfParagraphLine>();
        var regionStarts = new HashSet<PdfParagraphLine>();
        var tableStarts = new Dictionary<PdfParagraphLine, PdfContentBlock>();
        var tableLines = new HashSet<PdfParagraphLine>();
        foreach (var page in graph.Pages())
        {
            var output = new PdfContentPage { PageNumber = page.Number };
            result.Pages.Add(output);
            var parsed = new PdfPageInterpreter(graph, page);
            void Report(PdfDiagnosticCode code, string message, IEnumerable<PdfReference>? references = null)
            {
                result.Diagnostics.Add(new PdfDiagnostic
                {
                    Code = code,
                    Severity = PdfDiagnosticSeverity.Warning,
                    PageNumber = page.Number,
                    Message = $"Page {page.Number}: {message}",
                    SourceObjects = (references ?? page.Streams.Select(s => s.Id)).Distinct().ToArray()
                });
            }

            if (parsed.Images.Count > 0)
            {
                Report(PdfDiagnosticCode.ImagesOmitted, "images are omitted; image asset export is not implemented.",
                    parsed.Images.SelectMany(i => i.ImageObjects).Concat(page.Streams.Select(s => s.Id)));
            }

            if (parsed.ImageWarning is not null)
            {
                Report(PdfDiagnosticCode.ImageExtractionIncomplete, $"image interpretation is incomplete: {parsed.ImageWarning}");
            }

            try
            {
                var lines = PdfParagraphExtractor.Extract(page, parsed, columnGapFactor: h.MarkdownColumnGapFactor, heuristics: h).SelectMany(p => p.Lines)
                    .OrderByDescending(l => l.Baseline).ThenBy(l => l.Left).ToList();
                lines = JoinFieldValues(lines, h);
                foreach (var table in PdfTableDetector.Detect(lines, h))
                {
                    tableStarts.Add(table.SourceLines[0], table);
                    tableLines.UnionWith(table.SourceLines);
                }

                var layout = PdfLayoutAnalyzer.Analyze(lines, tableLines, page.Number, h);
                lines = layout.Lines;
                regionStarts.UnionWith(layout.RegionStarts);
                ambiguousLines.UnionWith(layout.AmbiguousLines);
                result.Diagnostics.AddRange(layout.Diagnostics);

                if (lines.Count == 0 && parsed.Images.Count > 0)
                {
                    Report(PdfDiagnosticCode.OcrRequired, "no extractable text; OCR is not implemented.");
                }

                pageLines.Add((output, lines));
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                Report(parsed.TextWarning is not null ? PdfDiagnosticCode.TextExtractionFailed : PdfDiagnosticCode.UnsupportedTextLayout,
                    $"text omitted. {ex.Message}");
            }
        }

        var allLines = pageLines.SelectMany(p => p.Lines).Where(l => !tableLines.Contains(l)).ToList();
        var bodyHeight = options.BodyTextHeight ?? allLines.GroupBy(l => Math.Round(l.Height, h.HeightRoundingDigits))
            .OrderByDescending(g => g.Sum(l => l.Text.Count(c => !char.IsWhiteSpace(c))))
            .ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault(12);
        foreach (var (page, lines) in pageLines)
        {
            foreach (var line in lines)
            {
                if (tableLines.Contains(line))
                {
                    if (tableStarts.TryGetValue(line, out var table))
                    {
                        page.Blocks.Add(table);
                    }

                    continue;
                }

                var previous = regionStarts.Contains(line) ? null : page.Blocks.LastOrDefault();
                var marker = ListMarker.Match(line.Text);
                var bold = IsBold(line);
                var gapAbove = previous is null ? double.PositiveInfinity : previous.SourceLines[^1].Baseline - line.Baseline;
                var isHeading = !line.Text.Contains(':') && (line.Height >= bodyHeight * h.HeadingSizeRatio
                    || (bold && line.Height >= bodyHeight * h.BoldHeadingSizeRatio && gapAbove > line.Height * h.HeadingGapFactor));
                // Wrapped bold headings need not repeat the extra spacing of their first line.
                if (previous?.Kind == PdfContentBlockKind.Heading && bold && IsBold(previous.SourceLines[^1])
                    && Compatible(previous.SourceLines[^1], line, centered: true, h))
                {
                    isHeading = true;
                }

                var kind = isHeading ? PdfContentBlockKind.Heading
                    : marker.Success ? PdfContentBlockKind.ListItem : PdfContentBlockKind.Paragraph;
                var canJoin = previous is not null && !line.Text.Contains(':') && !previous.Text.Contains(':')
                    && !ambiguousLines.Contains(line)
                    && !ambiguousLines.Contains(previous.SourceLines[^1]) && Compatible(previous.SourceLines[^1], line, kind == PdfContentBlockKind.Heading, h)
                    && ((previous.Kind == kind && kind != PdfContentBlockKind.ListItem)
                        || (previous.Kind == PdfContentBlockKind.ListItem && kind == PdfContentBlockKind.Paragraph
                            && line.Left > previous.SourceLines[0].Left + line.Height * h.ListIndentFactor));
                if (canJoin)
                {
                    previous!.Text = Join(previous.Text, line.Text.Trim(), options.JoinHyphenatedWords);
                    previous.SourceLines.Add(line);
                }
                else
                {
                    page.Blocks.Add(new PdfContentBlock
                    {
                        Kind = kind,
                        Text = kind == PdfContentBlockKind.ListItem ? marker.Groups["body"].Value.Trim() : line.Text.Trim(),
                        ListNumber = kind == PdfContentBlockKind.ListItem && marker.Groups["number"].Success ? marker.Groups["number"].Value : null,
                        SourceLines = [line]
                    });
                }
            }
        }

        var headings = result.Pages.SelectMany(p => p.Blocks).Where(b => b.Kind == PdfContentBlockKind.Heading).ToList();
        var sizes = headings.Select(b => Math.Round(b.SourceLines[0].Height, h.HeightRoundingDigits)).Distinct().OrderDescending().ToList();
        foreach (var heading in headings)
        {
            heading.HeadingLevel = Math.Min(6, sizes.IndexOf(Math.Round(heading.SourceLines[0].Height, h.HeightRoundingDigits)) + 1);
        }

        return result;
    }

    private static List<PdfParagraphLine> JoinFieldValues(List<PdfParagraphLine> lines, PdfHeuristicOptions h)
    {
        var rows = new List<List<PdfParagraphLine>>();
        foreach (var line in lines)
        {
            if (rows.Count == 0 || Math.Abs(rows[^1][0].Baseline - line.Baseline) > Math.Min(rows[^1][0].Height, line.Height) * h.RowBaselineTolerance)
            {
                rows.Add([]);
            }

            rows[^1].Add(line);
        }

        return rows.SelectMany(row =>
        {
            row.Sort((a, b) => a.Left.CompareTo(b.Left));
            for (var i = 0; i + 1 < row.Count; i++)
            {
                if (row[i].Text.Trim() is "$" or "\u00a3" or "\u20ac" && PdfTableDetector.IsNumeric(row[i + 1].Text)
                    && row[i + 1].Left - row[i].Right <= row[i].Height * h.CurrencyGapFactor)
                {
                    row[i] = new PdfParagraphLine
                    {
                        Text = row[i].Text.Trim() + row[i + 1].Text.Trim(),
                        Left = row[i].Left, Right = row[i + 1].Right,
                        Baseline = row[i].Baseline, Height = row[i].Height,
                        Runs = row[i].Runs.Concat(row[i + 1].Runs).ToList()
                    };
                    row.RemoveAt(i + 1);
                }
            }

            if (row.Count == 2 && row[0].Text.TrimEnd().EndsWith(':') && !row[1].Text.Contains(':'))
            {
                return new List<PdfParagraphLine> { new()
                {
                    Text = row[0].Text.TrimEnd() + " " + row[1].Text.Trim(),
                    Left = row[0].Left,
                    Right = row[1].Right,
                    Baseline = row[0].Baseline,
                    Height = Math.Max(row[0].Height, row[1].Height),
                    Runs = row.SelectMany(l => l.Runs).ToList()
                } };
            }

            return row;
        }).ToList();
    }

    private static bool IsBold(PdfParagraphLine line) => line.Runs.Count > 0 && line.Runs.All(r =>
        r.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true
        || r.FontName?.Contains("Black", StringComparison.OrdinalIgnoreCase) == true);

    private static bool Compatible(PdfParagraphLine previous, PdfParagraphLine next, bool centered, PdfHeuristicOptions h)
    {
        var height = Math.Max(previous.Height, next.Height);
        var gap = previous.Baseline - next.Baseline;
        var aligned = Math.Abs(previous.Left - next.Left) <= height * h.LeftAlignmentTolerance;
        if (centered)
        {
            aligned |= Math.Abs((previous.Left + previous.Right - next.Left - next.Right) / 2) <= height * h.CenterAlignmentTolerance;
        }

        return gap > height * h.MinimumLineGapFactor && gap <= height * h.MaximumLineGapFactor && aligned
            && Math.Min(previous.Height, next.Height) / height >= h.BlockSizeRatio
            && (!centered || IsBold(previous) == IsBold(next));
    }

    private static string Join(string previous, string next, bool dehyphenate)
    {
        if (dehyphenate && previous.Length > 1 && next.Length > 0 && previous[^1] == '-'
            && char.IsLetter(previous[^2]) && char.IsLower(next[0]))
        {
            return previous[..^1] + next;
        }

        if (previous.EndsWith('-') && next.Length > 0 && char.IsDigit(next[0]))
        {
            return previous + next;
        }

        return previous + " " + next;
    }
}
