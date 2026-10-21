namespace PdfObjectKit.Options;

/// <summary>Immutable heuristic settings. Defaults retain existing extraction and Markdown behavior.</summary>
public sealed class PdfHeuristicOptions
{
    /// <summary>Maximum baseline difference for a line, relative to the smaller text height.</summary>
    public double LineBaselineTolerance { get; init; } = 0.25;

    /// <summary>Maximum smaller/larger height ratio for superscript grouping.</summary>
    public double SuperscriptSizeRatio { get; init; } = 0.8;

    /// <summary>Superscript baseline tolerance relative to the larger height.</summary>
    public double SuperscriptBaselineTolerance { get; init; } = 0.5;

    /// <summary>Horizontal gap splitting standalone paragraph extraction, in text heights.</summary>
    public double TextColumnGapFactor { get; init; } = 2;

    /// <summary>Horizontal gap splitting Markdown analysis fragments, in text heights.</summary>
    public double MarkdownColumnGapFactor { get; init; } = 0.8;

    /// <summary>Minimum gap for an inferred space, in text heights.</summary>
    public double WordGapFactor { get; init; } = 0.15;

    /// <summary>Minimum baseline gap when joining consecutive lines, in text heights.</summary>
    public double MinimumLineGapFactor { get; init; } = 0.5;

    /// <summary>Maximum baseline gap when joining consecutive lines, in text heights.</summary>
    public double MaximumLineGapFactor { get; init; } = 1.6;

    /// <summary>Minimum smaller/larger height ratio for paragraph extraction.</summary>
    public double ParagraphSizeRatio { get; init; } = 0.8;

    /// <summary>Minimum smaller/larger height ratio for analyzed block continuation.</summary>
    public double BlockSizeRatio { get; init; } = 0.9;

    /// <summary>Maximum left-edge difference when joining lines, in text heights.</summary>
    public double LeftAlignmentTolerance { get; init; } = 1.5;

    /// <summary>Maximum center difference for wrapped headings, in text heights.</summary>
    public double CenterAlignmentTolerance { get; init; } = 0.5;

    /// <summary>Minimum heading height relative to estimated body text.</summary>
    public double HeadingSizeRatio { get; init; } = 1.25;

    /// <summary>Minimum bold heading height relative to body text.</summary>
    public double BoldHeadingSizeRatio { get; init; } = 0.95;

    /// <summary>Minimum preceding gap for a body-sized bold heading, in text heights.</summary>
    public double HeadingGapFactor { get; init; } = 1.6;

    /// <summary>Minimum indentation for a wrapped list item, in text heights.</summary>
    public double ListIndentFactor { get; init; } = 0.3;

    /// <summary>Baseline tolerance for tables and field/value rows, relative to smaller height.</summary>
    public double RowBaselineTolerance { get; init; } = 0.35;

    /// <summary>Maximum gap joining a currency symbol and number, in text heights.</summary>
    public double CurrencyGapFactor { get; init; } = 3;

    /// <summary>Vertical whitespace separating layout regions, in text heights.</summary>
    public double RegionGapFactor { get; init; } = 2.5;

    /// <summary>Minimum continuous gutter separating columns, in typical text heights.</summary>
    public double ColumnGutterFactor { get; init; } = 2;

    /// <summary>Minimum vertical overlap between columns, in typical text heights.</summary>
    public double ColumnOverlapFactor { get; init; } = 0.5;

    /// <summary>Baseline proximity indicating unresolved parallel fragments, in smaller text heights.</summary>
    public double AmbiguousBaselineTolerance { get; init; } = 0.5;

    /// <summary>Maximum baseline gap between table rows, in text heights.</summary>
    public double TableRowGapFactor { get; init; } = 3.2;

    /// <summary>Maximum header-to-data gap for a single-row table, in text heights.</summary>
    public double SingleRowTableGapFactor { get; init; } = 2.2;

    /// <summary>Minimum description indentation for a wrapped table cell, in text heights.</summary>
    public double TableContinuationIndentFactor { get; init; } = 0.25;

    /// <summary>Minimum smaller/larger height ratio for matching table cells.</summary>
    public double TableSizeRatio { get; init; } = 0.7;

    /// <summary>Maximum edge difference matching a cell to a column, in text heights.</summary>
    public double TableAlignmentTolerance { get; init; } = 1.5;

    /// <summary>Minimum lines in each inferred column.</summary>
    public int MinimumColumnLines { get; init; } = 2;

    /// <summary>Minimum rows for repeated-alignment table evidence; styled single-row tables are a separate rule.</summary>
    public int MinimumTableRows { get; init; } = 3;

    /// <summary>Minimum numeric data cells supporting a table column.</summary>
    public int MinimumNumericCells { get; init; } = 2;

    /// <summary>Decimal places grouping text heights for body estimation and heading levels.</summary>
    public int HeightRoundingDigits { get; init; } = 1;

    /// <summary>Allows a distinct styled header followed by just one data row.</summary>
    public bool EnableSingleRowTables { get; init; } = true;

    internal void Validate()
    {
        if (!double.IsFinite(LineBaselineTolerance) || LineBaselineTolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LineBaselineTolerance), "Must be finite and positive.");
        }

        if (!double.IsFinite(SuperscriptSizeRatio) || SuperscriptSizeRatio <= 0 || SuperscriptSizeRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SuperscriptSizeRatio), "Must be finite and in (0, 1].");
        }

        if (!double.IsFinite(SuperscriptBaselineTolerance) || SuperscriptBaselineTolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SuperscriptBaselineTolerance), "Must be finite and positive.");
        }

        if (!double.IsFinite(TextColumnGapFactor) || TextColumnGapFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TextColumnGapFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(MarkdownColumnGapFactor) || MarkdownColumnGapFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MarkdownColumnGapFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(WordGapFactor) || WordGapFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WordGapFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(MinimumLineGapFactor) || MinimumLineGapFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumLineGapFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(MaximumLineGapFactor) || MaximumLineGapFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumLineGapFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(ParagraphSizeRatio) || ParagraphSizeRatio <= 0 || ParagraphSizeRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ParagraphSizeRatio), "Must be finite and in (0, 1].");
        }

        if (!double.IsFinite(BlockSizeRatio) || BlockSizeRatio <= 0 || BlockSizeRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockSizeRatio), "Must be finite and in (0, 1].");
        }

        if (!double.IsFinite(LeftAlignmentTolerance) || LeftAlignmentTolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LeftAlignmentTolerance), "Must be finite and positive.");
        }

        if (!double.IsFinite(CenterAlignmentTolerance) || CenterAlignmentTolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CenterAlignmentTolerance), "Must be finite and positive.");
        }

        if (!double.IsFinite(HeadingSizeRatio) || HeadingSizeRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HeadingSizeRatio), "Must be finite and positive.");
        }

        if (!double.IsFinite(BoldHeadingSizeRatio) || BoldHeadingSizeRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BoldHeadingSizeRatio), "Must be finite and positive.");
        }

        if (!double.IsFinite(HeadingGapFactor) || HeadingGapFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HeadingGapFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(ListIndentFactor) || ListIndentFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ListIndentFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(RowBaselineTolerance) || RowBaselineTolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RowBaselineTolerance), "Must be finite and positive.");
        }

        if (!double.IsFinite(CurrencyGapFactor) || CurrencyGapFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CurrencyGapFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(RegionGapFactor) || RegionGapFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RegionGapFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(ColumnGutterFactor) || ColumnGutterFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ColumnGutterFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(ColumnOverlapFactor) || ColumnOverlapFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ColumnOverlapFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(AmbiguousBaselineTolerance) || AmbiguousBaselineTolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AmbiguousBaselineTolerance), "Must be finite and positive.");
        }

        if (!double.IsFinite(TableRowGapFactor) || TableRowGapFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TableRowGapFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(SingleRowTableGapFactor) || SingleRowTableGapFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SingleRowTableGapFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(TableContinuationIndentFactor) || TableContinuationIndentFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TableContinuationIndentFactor), "Must be finite and positive.");
        }

        if (!double.IsFinite(TableSizeRatio) || TableSizeRatio <= 0 || TableSizeRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(TableSizeRatio), "Must be finite and in (0, 1].");
        }

        if (!double.IsFinite(TableAlignmentTolerance) || TableAlignmentTolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TableAlignmentTolerance), "Must be finite and positive.");
        }

        if (MinimumColumnLines < 2 || MinimumColumnLines > 1000000)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumColumnLines), "Must be between 2 and 1000000.");
        }

        if (MinimumTableRows < 2 || MinimumTableRows > 1000000)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumTableRows), "Must be between 2 and 1000000.");
        }

        if (MinimumNumericCells < 1 || MinimumNumericCells > 1000000)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumNumericCells), "Must be between 1 and 1000000.");
        }

        if (HeightRoundingDigits < 0 || HeightRoundingDigits > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(HeightRoundingDigits), "Must be between 0 and 6.");
        }

        if (MinimumLineGapFactor >= MaximumLineGapFactor)
        {
            throw new ArgumentException("MinimumLineGapFactor must be less than MaximumLineGapFactor.");
        }
    }
}
