namespace Practical.PdfJson;

public sealed class PdfObjectDocument
{
    public string Format { get; set; } = "pdf-object-graph";

    public int SchemaVersion { get; set; } = 1;

    public string PdfVersion { get; set; } = "1.7";

    public required PdfValue Trailer { get; set; }

    public List<PdfIndirectObject> Objects { get; set; } = [];

    // Text edits are applied by WritePdf; references remain informational.
    public List<PdfPageTextIndex> TextIndex { get; set; } = [];

    public List<PdfPageImageIndex> ImageIndex { get; set; } = [];
}

public sealed class PdfPageImageIndex
{
    public int PageNumber { get; set; }

    public List<PdfImageOccurrence> Images { get; set; } = [];

    public string? Warning { get; set; }
}

public sealed class PdfImageOccurrence
{
    public int ImageNumber { get; set; }

    public bool IsInline { get; set; }

    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    public int BitsPerComponent { get; set; }

    public bool IsImageMask { get; set; }

    // Corners in unrotated PDF user space: bottom-left, bottom-right, top-right, top-left.
    public List<PdfImagePoint> Corners { get; set; } = [];

    // Exact invoked resource; the list shape is retained for JSON compatibility.
    public List<PdfReference> ImageObjects { get; set; } = [];

    public string EncodedDataSha256 { get; set; } = "";

    public string? InlineDataBase64 { get; set; }
}

public sealed record PdfImagePoint(double X, double Y);
public sealed class PdfPageTextIndex
{
    public int PageNumber { get; set; }

    public string Text { get; set; } = "";

    public List<PdfReference> ContentStreams { get; set; } = [];

    // Resource candidates, including nested forms; not necessarily invoked by the page.
    public List<PdfReference> FormStreams { get; set; } = [];

    public string? Warning { get; set; }

    public List<PdfTextRun>? Runs { get; set; }

    public string? RunWarning { get; set; }
}

public sealed class PdfTextRun
{
    public required PdfReference StreamObject { get; set; }

    public int OperatorIndex { get; set; }

    // -1 for Tj; zero-based array index for a TJ string.
    public int OperandIndex { get; set; }

    public required string StreamSha256 { get; set; }

    public required string OriginalText { get; set; }

    public required string Text { get; set; }

    public string? FontName { get; set; }

    public double FontSize { get; set; }
}

public sealed record PdfReference(long ObjectNumber, int Generation = 0);
public sealed record PdfIndirectObject(PdfReference Id, PdfValue Value);
// A discriminated union: only the properties belonging to Type may be populated.
// Names and dictionary keys are decoded PDF name bytes mapped one-to-one to Latin-1.
// Strings are bytes, not .NET text: their encoding is determined by PDF context.
public sealed class PdfValue
{
    public required string Type { get; set; }

    public string? Number { get; set; }

    public bool? Boolean { get; set; }

    public string? Name { get; set; }

    public string? Base64 { get; set; }

    public PdfReference? Reference { get; set; }

    public List<PdfValue>? Items { get; set; }

    public Dictionary<string, PdfValue>? Entries { get; set; }
}
