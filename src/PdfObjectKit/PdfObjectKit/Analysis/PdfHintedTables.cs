using PdfObjectKit.Models;
using PdfObjectKit.Options;

namespace PdfObjectKit.Analysis;

internal static class PdfHintedTables
{
    public static void Validate(IReadOnlyList<PdfTableHint> hints, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(hints);
        if (hints.Count > 20)
        {
            throw new ArgumentException("Provide at most 20 table hints.");
        }
        for (var i = 0; i < hints.Count; i++)
        {
            var hint = hints[i];
            if (hint is null || hint.PageNumber < 1 || hint.PageNumber > pageCount
                || !double.IsFinite(hint.X) || !double.IsFinite(hint.Y)
                || !double.IsFinite(hint.Width) || !double.IsFinite(hint.Height)
                || hint.Width <= 0 || hint.Height <= 0
                || !double.IsFinite(hint.X + hint.Width) || !double.IsFinite(hint.Y + hint.Height))
            {
                throw new ArgumentException("Table hints require a valid page and finite, positive dimensions.");
            }
            if (hints.Take(i).Any(other => other.PageNumber == hint.PageNumber
                && other.X < hint.X + hint.Width && hint.X < other.X + other.Width
                && other.Y < hint.Y + hint.Height && hint.Y < other.Y + other.Height))
            {
                throw new ArgumentException("Table hints on the same page must not overlap.");
            }
            if (hint.ColumnBoundaries is { } boundaries)
            {
                var previous = hint.X;
                if (boundaries.Count > 50)
                {
                    throw new ArgumentException("Provide at most 50 column separators per table.");
                }
                foreach (var boundary in boundaries)
                {
                    if (!double.IsFinite(boundary) || boundary <= previous || boundary >= hint.X + hint.Width)
                    {
                        throw new ArgumentException("Column separators must be strictly increasing and inside the table region.");
                    }
                    previous = boundary;
                }
            }
        }
    }

    public static List<PdfContentBlock> Build(List<PdfParagraphLine> lines, IEnumerable<PdfTableHint> hints, PdfHeuristicOptions h, Action<string>? warn = null)
    {
        var result = new List<PdfContentBlock>();
        foreach (var hint in hints)
        {
            var selected = lines.Where(line => (line.Left + line.Right) / 2 >= hint.X
                && (line.Left + line.Right) / 2 < hint.X + hint.Width
                && line.Baseline + line.Height / 2 >= hint.Y
                && line.Baseline + line.Height / 2 < hint.Y + hint.Height)
                .OrderByDescending(line => line.Baseline).ThenBy(line => line.Left).ToList();
            if (selected.Count == 0)
            {
                throw new ArgumentException($"Table hint on page {hint.PageNumber} contains no extractable text. Adjust the region; scanned tables require OCR.");
            }
            var rows = new List<List<PdfParagraphLine>>();
            foreach (var line in selected)
            {
                if (rows.Count == 0 || Math.Abs(rows[^1][0].Baseline - line.Baseline)
                    > Math.Min(rows[^1][0].Height, line.Height) * h.RowBaselineTolerance)
                {
                    rows.Add([]);
                }
                rows[^1].Add(line);
            }
            var seed = rows.OrderByDescending(row => row.Count).First().OrderBy(line => line.Left).ToList();
            var boundaries = hint.ColumnBoundaries?.ToArray()
                ?? seed.Zip(seed.Skip(1), (left, right) => (left.Right + right.Left) / 2).ToArray();
            if (hint.ColumnBoundaries is not null && selected.Any(line => boundaries.Any(boundary => line.Left < boundary && boundary < line.Right)))
            {
                warn?.Invoke("A manual table column separator crosses a text fragment. The whole fragment was assigned by its center; adjust the separators and review the table.");
            }
            var table = new PdfTable();
            foreach (var row in rows)
            {
                var output = new PdfTableRow
                {
                    IsHeader = table.Rows.Count == 0 && hint.FirstRowIsHeader,
                    Cells = Enumerable.Range(0, boundaries.Length + 1).Select(_ => new PdfTableCell()).ToList(),
                };
                foreach (var line in row.OrderBy(line => line.Left))
                {
                    var column = boundaries.Count(boundary => (line.Left + line.Right) / 2 > boundary);
                    var cell = output.Cells[column];
                    cell.Text += (cell.Text.Length == 0 ? "" : " ") + line.Text.Trim();
                    cell.SourceLines.Add(line);
                }
                table.Rows.Add(output);
            }
            result.Add(new PdfContentBlock { Kind = PdfContentBlockKind.Table, Table = table, SourceLines = selected });
        }
        return result;
    }
}
