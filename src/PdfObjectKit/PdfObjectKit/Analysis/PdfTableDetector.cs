using PdfObjectKit.Options;
using System.Text.RegularExpressions;
using PdfObjectKit.Models;

namespace PdfObjectKit.Analysis;

/// <summary>Conservative borderless-table detection from repeated horizontal alignment.</summary>
internal static class PdfTableDetector
{
    private static readonly Regex Numeric = new(@"^[\p{Sc}]?\s*[-+]?\(?[0-9][0-9,. /:%-]*\)?\s*[\p{Sc}]?$", RegexOptions.CultureInvariant);

    internal static bool IsNumeric(string text) => Numeric.IsMatch(text.Trim());

    public static List<PdfContentBlock> Detect(List<PdfParagraphLine> lines, PdfHeuristicOptions? heuristics = null)
    {
        var h = heuristics ?? new();
        h.Validate();
        var rows = new List<List<PdfParagraphLine>>();
        foreach (var line in lines.OrderByDescending(l => l.Baseline).ThenBy(l => l.Left))
        {
            if (rows.Count == 0 || Math.Abs(rows[^1][0].Baseline - line.Baseline) > Math.Min(rows[^1][0].Height, line.Height) * h.RowBaselineTolerance)
            {
                rows.Add([]);
            }

            rows[^1].Add(line);
        }

        foreach (var row in rows)
        {
            row.Sort((a, b) => a.Left.CompareTo(b.Left));
        }

        var result = new List<PdfContentBlock>();
        for (var start = 0; start < rows.Count; start++)
        {
            var seed = rows[start];
            if (seed.Count < 2)
            {
                continue;
            }

            var group = new List<List<PdfParagraphLine>> { seed };
            var mapped = new List<int[]> { Enumerable.Range(0, seed.Count).ToArray() };
            for (var end = start + 1; end < rows.Count; end++)
            {
                var row = rows[end];
                var height = Math.Max(seed.Max(l => l.Height), row.Max(l => l.Height));
                if (group[^1][0].Baseline - row[0].Baseline > height * h.TableRowGapFactor || row.Count > seed.Count)
                {
                    break;
                }

                var indices = row.Select(line => Match(line, seed, h)).ToArray();
                if (indices.Any(i => i < 0) || indices.Distinct().Count() != indices.Length)
                {
                    break;
                }

                var firstDataRowBelowHeader = h.EnableSingleRowTables && group.Count == 1 && seed.All(Bold)
                    && row.Any(l => !Bold(l)) && seed[0].Baseline - row[0].Baseline <= height * h.SingleRowTableGapFactor;
                if (row.Count == 1 && !IsNumeric(row[0].Text) && !firstDataRowBelowHeader
                    && (indices[0] != 0 || row[0].Left <= seed[0].Left + row[0].Height * h.TableContinuationIndentFactor))
                {
                    break;
                }

                group.Add(row);
                mapped.Add(indices);
            }

            var styledHeader = seed.All(Bold) && group.Skip(1).Any(r => r.Any(l => !Bold(l)));
            var singleDataRow = h.EnableSingleRowTables && group.Count == 2 && styledHeader
                && seed[0].Baseline - group[1][0].Baseline <= Math.Max(seed.Max(l => l.Height), group[1].Max(l => l.Height)) * h.SingleRowTableGapFactor;
            if (!singleDataRow && (group.Count < h.MinimumTableRows || group.Count(r => r.Count >= 2) < h.MinimumTableRows))
            {
                continue;
            }

            var columns = Enumerable.Range(0, seed.Count).Select(c => group.SelectMany((row, r) =>
                row.Where((line, i) => mapped[r][i] == c)).ToList()).ToList();
            var numericColumns = Enumerable.Range(1, seed.Count - 1)
                .Where(c => columns[c].Skip(1).Count(l => IsNumeric(l.Text)) >= h.MinimumNumericCells).ToList();
            if (numericColumns.Count == 0 && !styledHeader)
            {
                continue;
            }

            // Empty cells do not remove columns established by the header.
            var lefts = columns.Select(c => c.Min(l => l.Left)).ToArray();
            var rights = columns.Select(c => c.Max(l => l.Right)).ToArray();
            if (Enumerable.Range(1, seed.Count - 1).Any(c => lefts[c] <= rights[c - 1]))
            {
                continue;
            }

            var table = new PdfTable();
            for (var r = 0; r < group.Count; r++)
            {
                var fragments = group[r];
                var previousRow = r > 0 ? group[r - 1] : null;
                var continuation = previousRow is not null && fragments.Count == 1 && !IsNumeric(fragments[0].Text)
                    && mapped[r][0] == 0 && table.Rows.Count > 0 && !table.Rows[^1].IsHeader
                    && fragments[0].Left > lefts[0] + fragments[0].Height * h.TableContinuationIndentFactor
                    && previousRow[0].Baseline - fragments[0].Baseline <= fragments[0].Height * h.MaximumLineGapFactor;
                if (continuation)
                {
                    var cell = table.Rows[^1].Cells[0];
                    cell.Text += "\n" + fragments[0].Text.Trim();
                    cell.SourceLines.Add(fragments[0]);
                    continue;
                }

                var output = new PdfTableRow
                {
                    IsHeader = r == 0 && (styledHeader || numericColumns.Any(c => !IsNumeric(seed[c].Text))),
                    Cells = Enumerable.Range(0, seed.Count).Select(_ => new PdfTableCell()).ToList()
                };
                for (var c = 0; c < fragments.Count; c++)
                {
                    var cell = output.Cells[mapped[r][c]];
                    cell.Text = fragments[c].Text.Trim();
                    cell.SourceLines.Add(fragments[c]);
                }

                table.Rows.Add(output);
            }

            result.Add(new PdfContentBlock
            {
                Kind = PdfContentBlockKind.Table,
                Table = table,
                SourceLines = group.SelectMany(r => r).ToList()
            });
            start += group.Count - 1;
        }

        return result;
    }

    private static int Match(PdfParagraphLine line, List<PdfParagraphLine> seed, PdfHeuristicOptions h)
    {
        var matches = Enumerable.Range(0, seed.Count).Where(c =>
            Math.Min(line.Height, seed[c].Height) / Math.Max(line.Height, seed[c].Height) >= h.TableSizeRatio
            && (Math.Abs(line.Left - seed[c].Left) <= line.Height * h.TableAlignmentTolerance
                || Math.Abs(line.Right - seed[c].Right) <= line.Height * h.TableAlignmentTolerance
                || (line.Left < seed[c].Right && line.Right > seed[c].Left))
            && (c == 0 || line.Left > seed[c - 1].Right)
            && (c == seed.Count - 1 || line.Right < seed[c + 1].Left)).ToList();
        return matches.Count == 1 ? matches[0] : -1;
    }

    private static bool Bold(PdfParagraphLine line) => line.Runs.Count > 0
        && line.Runs.All(r => r.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true);
}
