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

    /// <summary>Builds UI selections without requiring all page text to be horizontal.</summary>
    public static List<PdfTextSelection> ExtractSelectionLines(PdfObjectDocument document, int pageNumber)
    {
        PdfDocumentValidator.Validate(document);
        var graph = new PdfGraph(document);
        var pages = graph.Pages();
        if (pageNumber < 1 || pageNumber > pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }
        var page = pages[pageNumber - 1];
        var parsed = new PdfPageInterpreter(graph, page);
        var selections = Extract(page, parsed, horizontalOnly: true).SelectMany(paragraph => paragraph.Lines)
            .Select(line => new PdfTextSelection(line.Text,
                [new(line.Left, line.Baseline - line.Height * .2), new(line.Right, line.Baseline - line.Height * .2), new(line.Right, line.Baseline + line.Height), new(line.Left, line.Baseline + line.Height)], line.Runs)).ToList();
        var hashes = parsed.TextStreams.ToDictionary(item => item.Key,
            item => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(item.Value.Base64!))));
        foreach (var slot in parsed.Slots.Where(slot => !slot.IsHorizontal && !string.IsNullOrWhiteSpace(slot.Text)))
        {
            if (parsed.TextStreams.TryGetValue(slot.Stream, out var stream)
                && stream.Entries?.GetValueOrDefault("PdfObjectKitWatermark")?.Type == "string")
            {
                continue;
            }
            var upX = slot.TextMatrix.C * slot.FontSize;
            var upY = slot.TextMatrix.D * slot.FontSize;
            if (Math.Abs(upX) + Math.Abs(upY) < 1e-9)
            {
                continue;
            }
            PdfImagePoint Offset(PdfImagePoint point, double factor) => new(point.X + upX * factor, point.Y + upY * factor);
            var line = BuildLine([slot], hashes, new());
            selections.Add(new(slot.Text, [Offset(slot.BaselineStart, -.2), Offset(slot.BaselineEnd, -.2), Offset(slot.BaselineEnd, 1), Offset(slot.BaselineStart, 1)], line.Runs));
        }
        return selections;
    }

    internal static List<PdfParagraph> Extract(NativePage page, PdfPageInterpreter parsed, double columnGapFactor = 2, PdfHeuristicOptions? heuristics = null, IReadOnlyList<PdfTableHint>? tableHints = null, bool horizontalOnly = false)
    {
        var h = heuristics ?? new();
        h.Validate();
        var pageNumber = page.Number;
        if (parsed.TextWarning is not null)
        {
            throw new InvalidDataException($"Cannot detect paragraphs: {parsed.TextWarning}");
        }

        // Watermarks are decorative Form content, not document paragraphs. Keep them in
        // raw text extraction/rendering, but do not let their angle suppress page text.
        var slots = parsed.Slots.Where(slot => !parsed.TextStreams.TryGetValue(slot.Stream, out var stream)
            || stream.Entries?.GetValueOrDefault("PdfObjectKitWatermark")?.Type != "string").ToList();
        if (!horizontalOnly && slots.Any(s => !s.IsHorizontal || s.Height <= 0))
        {
            throw new NotSupportedException("Paragraph detection requires horizontal text in page coordinates.");
        }

        if (horizontalOnly)
        {
            slots = slots.Where(slot => slot.IsHorizontal && slot.Height > 0).ToList();
        }

        var hashes = parsed.TextStreams.Select(s => (Id: s.Key, Value: s.Value)).ToDictionary(s => s.Id,
            s => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(s.Value.Base64!))));
        var rows = new List<List<NativeSlot>>();
        foreach (var slot in slots.Where(s => !string.IsNullOrWhiteSpace(s.Text))
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
        (PdfTableHint? Table, int Column) Cell(NativeSlot slot)
        {
            var x = (slot.BaselineStart.X + slot.BaselineEnd.X) / 2;
            var y = slot.BaselineStart.Y + slot.Height / 2;
            var hint = tableHints?.FirstOrDefault(hint => hint.PageNumber == pageNumber
                && x >= hint.X && x < hint.X + hint.Width && y >= hint.Y && y < hint.Y + hint.Height);
            return (hint, hint?.ColumnBoundaries?.Count(boundary => x > boundary) ?? 0);
        }
        foreach (var row in rows)
        {
            var segment = new List<NativeSlot>();
            foreach (var slot in row.OrderBy(s => s.BaselineStart.X))
            {
                if (segment.Count > 0 && (Cell(slot) != Cell(segment[^1])
                    || slot.BaselineStart.X - segment[^1].BaselineEnd.X
                    > Math.Max(slot.Height, segment[^1].Height) * columnGapFactor))
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
