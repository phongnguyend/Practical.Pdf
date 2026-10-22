using System.Text;
using PdfObjectKit.Analysis;
using PdfObjectKit.Models;

namespace PdfObjectKit.Converters;

public static class PdfMarkdownConverter
{
    public static PdfMarkdownResult Convert(PdfObjectDocument document, PdfMarkdownOptions? options = null)
    {
        var structure = PdfDocumentAnalyzer.Analyze(document, options);
        return new PdfMarkdownResult { Document = structure, Markdown = Render(structure, options) };
    }

    /// <summary>Renders structured blocks, allowing callers to correct heuristic classification first.</summary>
    public static string Render(PdfStructuredDocument document, PdfMarkdownOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new PdfMarkdownOptions();
        ArgumentNullException.ThrowIfNull(options.ImageRender);
        var result = new StringBuilder();
        foreach (var page in document.Pages)
        {
            PdfContentBlock? previous = null;
            var imageNumber = 0;
            foreach (var block in page.Blocks)
            {
                string? imageMarkdown = null;
                if (block.Kind == PdfContentBlockKind.Image)
                {
                    var image = block.Image ?? throw new ArgumentException("Image block requires image data.");
                    imageNumber++;
                    var occurrenceNumber = image.Source?.ImageNumber > 0 ? image.Source.ImageNumber : imageNumber;
                    imageMarkdown = options.ImageRender(image, page.PageNumber, occurrenceNumber)
                        ?? throw new InvalidOperationException("Image renderer returned null. Return an empty string to omit an image.");
                    if (string.IsNullOrWhiteSpace(imageMarkdown))
                    {
                        continue;
                    }
                }

                var sameList = previous?.Kind == PdfContentBlockKind.ListItem && block.Kind == PdfContentBlockKind.ListItem
                    && (previous.ListNumber is null) == (block.ListNumber is null);
                if (result.Length > 0)
                {
                    result.Append(sameList ? "\n" : "\n\n");
                }

                if (block.Kind == PdfContentBlockKind.Image)
                {
                    result.Append(imageMarkdown);
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
