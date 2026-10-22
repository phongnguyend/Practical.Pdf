using PdfObjectKit.Options;
using System.Security.Cryptography;
using System.Text;
using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
using PdfObjectKit.Validators;

namespace PdfObjectKit.Extractors;

/// <summary>Infers paragraphs from horizontal text placement, without modifying the document.</summary>
public static class PdfParagraphExtractor
{
    /// <summary>
    /// Detects paragraphs on a one-based page. Coordinates use unrotated PDF user space.
    /// Page run operator indices follow concatenated page content streams; Form run indices are local to their stream.
    /// FormOccurrenceId distinguishes repeated/nested placements within the page traversal.
    /// Results are ordered top-to-bottom, then left-to-right, not semantic column reading order.
    /// </summary>
    public static List<PdfParagraph> Extract(PdfObjectDocument document, int pageNumber, PdfHeuristicOptions? heuristics = null)
    {
        var h = heuristics ?? new();
        h.Validate();
        PdfDocumentValidator.Validate(document);
        var graph = new PdfGraph(document);
        var pages = graph.Pages();
        if (pageNumber < 1 || pageNumber > pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        var page = pages[pageNumber - 1];
        return Extract(page, new PdfPageInterpreter(graph, page), h.TextColumnGapFactor, h);
    }

    internal static List<PdfParagraph> Extract(NativePage page, PdfPageInterpreter parsed, double columnGapFactor = 2, PdfHeuristicOptions? heuristics = null)
    {
        var h = heuristics ?? new();
        h.Validate();
        var pageNumber = page.Number;
        if (parsed.TextWarning is not null)
        {
            throw new InvalidDataException($"Cannot detect paragraphs: {parsed.TextWarning}");
        }

        if (parsed.Slots.Any(s => !s.IsHorizontal || s.Height <= 0))
        {
            throw new NotSupportedException("Paragraph detection requires horizontal text in page coordinates.");
        }

        var hashes = parsed.TextStreams.Select(s => (Id: s.Key, Value: s.Value)).ToDictionary(s => s.Id,
            s => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(s.Value.Base64!))));
        var rows = new List<List<NativeSlot>>();
        foreach (var slot in parsed.Slots.Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .OrderByDescending(s => s.BaselineStart.Y).ThenBy(s => s.BaselineStart.X))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].BaselineStart.Y - slot.BaselineStart.Y)
                <= Math.Min(r[0].Height, slot.Height) * h.LineBaselineTolerance
                || (Math.Min(r[0].Height, slot.Height) / Math.Max(r[0].Height, slot.Height) < h.SuperscriptSizeRatio
                    && Math.Abs(r[0].BaselineStart.Y - slot.BaselineStart.Y) <= Math.Max(r[0].Height, slot.Height) * h.SuperscriptBaselineTolerance));
            if (row is null)
            {
                row = [];
                rows.Add(row);
            }

            row.Add(slot);
        }

        var lines = new List<PdfParagraphLine>();
        foreach (var row in rows)
        {
            var segment = new List<NativeSlot>();
            foreach (var slot in row.OrderBy(s => s.BaselineStart.X))
            {
                if (segment.Count > 0 && slot.BaselineStart.X - segment[^1].BaselineEnd.X
                    > Math.Max(slot.Height, segment[^1].Height) * columnGapFactor)
                {
                    lines.Add(BuildLine(segment, hashes, h));
                    segment = [];
                }

                segment.Add(slot);
            }

            lines.Add(BuildLine(segment, hashes, h));
        }

        var paragraphs = new List<PdfParagraph>();
        foreach (var line in lines.OrderByDescending(l => l.Baseline).ThenBy(l => l.Left))
        {
            var paragraph = paragraphs.Where(p => CanJoin(p.Lines[^1], line, h))
                .OrderBy(p => p.Lines[^1].Baseline - line.Baseline)
                .ThenBy(p => Math.Abs(p.Lines[^1].Left - line.Left)).FirstOrDefault();
            if (paragraph is null)
            {
                paragraph = new PdfParagraph { PageNumber = pageNumber };
                paragraphs.Add(paragraph);
            }

            paragraph.Lines.Add(line);
        }

        return paragraphs;
    }

    private static bool CanJoin(PdfParagraphLine previous, PdfParagraphLine next, PdfHeuristicOptions h)
    {
        var height = Math.Max(previous.Height, next.Height);
        var gap = previous.Baseline - next.Baseline;
        return gap > height * h.MinimumLineGapFactor && gap <= height * h.MaximumLineGapFactor
            && Math.Min(previous.Height, next.Height) / height >= h.ParagraphSizeRatio
            && Math.Abs(previous.Left - next.Left) <= height * h.LeftAlignmentTolerance
            && Math.Min(previous.Right, next.Right) > Math.Max(previous.Left, next.Left);
    }

    private static PdfParagraphLine BuildLine(List<NativeSlot> slots, Dictionary<PdfReference, string> hashes, PdfHeuristicOptions h)
    {
        var text = new StringBuilder();
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (i > 0 && slot.BaselineStart.X - slots[i - 1].BaselineEnd.X > slot.Height * h.WordGapFactor
                && text.Length > 0 && !char.IsWhiteSpace(text[^1]) && !char.IsWhiteSpace(slot.Text[0]))
            {
                text.Append(' ');
            }

            text.Append(slot.Text);
        }

        return new PdfParagraphLine
        {
            Text = text.ToString(),
            Left = slots.Min(s => s.BaselineStart.X),
            Right = slots.Max(s => s.BaselineEnd.X),
            Baseline = slots.MaxBy(s => s.Height)!.BaselineStart.Y,
            Height = slots.Max(s => s.Height),
            Runs = slots.Select(s => new PdfTextRun
            {
                StreamObject = s.Stream,
                FormOccurrenceId = s.FormOccurrenceId,
                OperatorIndex = s.Operation,
                OperandIndex = s.Token,
                StreamSha256 = hashes[s.Stream],
                Text = s.Text,
                FontName = s.Font.Name,
                FontSize = s.FontSize
            }).ToList()
        };
    }
}
