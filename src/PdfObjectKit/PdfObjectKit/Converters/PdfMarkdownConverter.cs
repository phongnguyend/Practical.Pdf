using System.Text;
using PdfObjectKit.Analysis;
using PdfObjectKit.Models;

namespace PdfObjectKit.Converters;

public static class PdfMarkdownConverter
{
    public static PdfMarkdownResult Convert(PdfObjectDocument document, PdfMarkdownOptions? options = null)
    {
        var structure = PdfDocumentAnalyzer.Analyze(document, options);
        return new PdfMarkdownResult { Document = structure, Markdown = Render(structure) };
    }

    /// <summary>Renders structured blocks, allowing callers to correct heuristic classification first.</summary>
    public static string Render(PdfStructuredDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new StringBuilder();
        foreach (var page in document.Pages)
        {
            PdfContentBlock? previous = null;
            foreach (var block in page.Blocks)
            {
                var sameList = previous?.Kind == PdfContentBlockKind.ListItem && block.Kind == PdfContentBlockKind.ListItem
                    && (previous.ListNumber is null) == (block.ListNumber is null);
                if (result.Length > 0)
                {
                    result.Append(sameList ? "\n" : "\n\n");
                }

                if (block.Kind == PdfContentBlockKind.Image)
                {
                    result.Append(ImageComment(block.Image ?? throw new ArgumentException("Image block requires image data.")));
                    previous = block;
                    continue;
                }

                if (block.Kind == PdfContentBlockKind.Table)
                {
                    result.Append(RenderTable(block.Table ?? throw new ArgumentException("Table block requires table data.")));
                    previous = block;
                    continue;
                }

                switch (block.Kind)
                {
                    case PdfContentBlockKind.Heading:
                        result.Append(new string('#', Math.Clamp(block.HeadingLevel, 1, 6))).Append(' ');
                        break;
                    case PdfContentBlockKind.ListItem:
                        var number = block.ListNumber;
                        if (number is not null && (number.Length is < 1 or > 9 || number.Any(c => c is < '0' or > '9')))
                        {
                            throw new ArgumentException("List numbers must contain one to nine ASCII digits.");
                        }

                        result.Append(number is null ? "- " : number + ". ");
                        break;
                }

                result.Append(Escape(block.Text));
                previous = block;
            }
        }

        return result.Length == 0 ? "" : result.Append('\n').ToString();
    }

    private static string ImageComment(PdfStructuredImage image)
    {
        var name = image.FileName?.Replace('\\', '/').Split('/')[^1].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            var extension = image.ContentType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "image/svg+xml" => ".svg",
                "image/bmp" => ".bmp",
                "image/tiff" => ".tiff",
                _ => ".bin"
            };
            var hash = image.EncodedDataSha256;
            if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            {
                throw new ArgumentException("An image without a filename requires a SHA-256 hash of its encoded data.");
            }

            name = "image-" + hash.ToLowerInvariant() + extension;
        }

        var fields = new List<string> { "image: " + EscapeComment(name) };
        if (!string.IsNullOrWhiteSpace(image.Caption))
        {
            fields.Add("caption: " + EscapeComment(image.Caption.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(image.AltText))
        {
            fields.Add("alt: " + EscapeComment(image.AltText.Trim()));
        }

        return "<!-- " + string.Join("; ", fields) + " -->";
    }

    private static string EscapeComment(string value) => string.Concat(value.Select(c => c switch
    {
        '&' => "&amp;",
        '<' => "&lt;",
        '>' => "&gt;",
        _ when char.IsControl(c) => " ",
        _ => c.ToString()
    })).Replace("--", "-&#45;");

    private static string RenderTable(PdfTable table)
    {
        if (table.Rows.Count == 0 || table.Rows.Any(r => r.Cells.Count == 0))
        {
            throw new ArgumentException("Tables must contain nonempty rows.");
        }

        if (table.Rows.SelectMany(r => r.Cells).Any(c => c.RowSpan < 1 || c.ColumnSpan < 1))
        {
            throw new ArgumentException("Cell spans must be positive.");
        }

        var width = table.Rows[0].Cells.Count;
        var simple = table.Rows.Skip(1).All(r => !r.IsHeader)
            && table.Rows.All(r => r.Cells.Count == width && r.Cells.All(c => c.RowSpan == 1 && c.ColumnSpan == 1));
        string MarkdownCell(string text) => string.Join("<br>", text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(Escape));
        if (simple)
        {
            var hasHeader = table.Rows[0].IsHeader;
            var dataRows = table.Rows.Skip(hasHeader ? 1 : 0).ToList();
            var headerCells = hasHeader
                ? table.Rows[0].Cells.Select(c => MarkdownCell(c.Text))
                : Enumerable.Repeat("", width);
            var rows = new List<string>
            {
                "| " + string.Join(" | ", headerCells) + " |"
            };
            var alignments = Enumerable.Range(0, width).Select(c =>
            {
                var values = dataRows.Select(r => r.Cells[c].Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                return values.Count > 0 && values.All(PdfTableDetector.IsNumeric) ? "---:" : "---";
            });
            rows.Add("| " + string.Join(" | ", alignments) + " |");
            rows.AddRange(dataRows.Select(r => "| " + string.Join(" | ", r.Cells.Select(c => MarkdownCell(c.Text))) + " |"));
            return string.Join("\n", rows);
        }

        // HTML preserves structures that pipe tables cannot represent.
        var html = new StringBuilder("<table>\n");
        foreach (var row in table.Rows)
        {
            html.Append("<tr>");
            foreach (var cell in row.Cells)
            {
                var tag = row.IsHeader ? "th" : "td";
                html.Append('<').Append(tag);
                if (cell.RowSpan > 1)
                {
                    html.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');
                }

                if (cell.ColumnSpan > 1)
                {
                    html.Append(" colspan=\"").Append(cell.ColumnSpan).Append('"');
                }

                html.Append('>').Append(System.Net.WebUtility.HtmlEncode(cell.Text)
                    .Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "<br>"))
                    .Append("</").Append(tag).Append('>');
            }

            html.Append("</tr>\n");
        }

        return html.Append("</table>").ToString();
    }

    private static string Escape(string text)
    {
        var result = new StringBuilder();
        foreach (var c in text)
        {
            if (c == '&')
            {
                result.Append("&amp;");
            }
            else if (c == '<')
            {
                result.Append("&lt;");
            }
            else if (c == '>')
            {
                result.Append("&gt;");
            }
            else if (c is '\n' or '\r' or '\t')
            {
                result.Append(' ');
            }
            else
            {
                if ("\\`*_[]#|".Contains(c))
                {
                    result.Append('\\');
                }

                result.Append(c);
            }
        }

        return System.Text.RegularExpressions.Regex.Replace(result.ToString(),
            @"^([0-9]{1,9})([.)])(?=\s)|^([-+])(?=\s)",
            match => match.Groups[3].Success ? "\\" + match.Value : match.Groups[1].Value + "\\" + match.Groups[2].Value);
    }
}
