# PdfObjectKit

C# / .NET 10 library and CLI for reading, editing, and writing PDF object graphs. They load a PDF's current indirect objects into memory, serialize them to JSON, and rebuild a PDF from that JSON. Reconstruction operates at the PDF object level, without re-rendering pages or embedding the entire original file. The CLI writes derived text, image, and paragraph data to separate JSON files by default.

## Markdown snapshot regression test

`PdfObjectKit.SnapshotTests` uses Verify.Xunit to compare the full Markdown generated from `Fixtures/input.pdf` with `Snapshots/MarkdownSnapshotTests.InputPdf.verified.md`. The fixture is a copy of the original CLI `bin/input.pdf`, kept with the tests so a clean checkout can run them. The initial baseline captures current behavior; it protects against changes, not every possible extraction error.

```powershell
dotnet test src/PdfObjectKit/PdfObjectKit.SnapshotTests --configuration Release
```

On a mismatch, Verify fails the test and writes `MarkdownSnapshotTests.InputPdf.received.md` beside the approved snapshot. Compare it with the `.verified.md` file. For an intentional change, review the entire diff, replace the verified file with the received file, rerun the test, and commit the updated baseline. Do not automatically accept snapshots in CI. Received files are ignored by Git; the PDF fixture and verified Markdown belong in source control. Snapshot Markdown uses LF line endings across platforms.

The GitHub workflow runs this test before uploading/publishing the NuGet package and uploads received files as `Markdown-snapshot-differences` on failure. The existing console regression tests remain separate. This fixture covers the invoice, bank statement, and loan form; other PDFs require additional fixtures to protect their behavior.

## Run

The object graph is read by the handwritten C# parser in `PdfObjectKit/Parsers`, with **no NuGet dependencies**. It handles PDF values, byte strings, indirect references, length-delimited streams, classic cross-reference tables, cross-reference streams, compressed object streams, hybrid references, and incremental revisions. Stream bytes remain Base64 in the model; numeric tokens retain their original precision.

To use the parser directly, reference `PdfObjectKit/PdfObjectKit.csproj`:

```csharp
using PdfObjectKit.Parsers;
var document = PdfNativeParser.Parse(File.ReadAllBytes("input.pdf"));
```

The parser, main library, and CLI have **no NuGet dependencies**. Content interpretation, text/image indexes, run editing, and TrueType embedding are implemented in C#. PDFsharp is used only by the test project as an independent reader. `PdfReader.Read(bytes)` reads only the object graph. Build indexes separately using `PdfTextExtractor.Extract(document)` and `PdfImageExtractor.Extract(document)`. To skip derived CLI exports:

```powershell
dotnet run --project src/PdfObjectKit/PdfObjectKit.Cli -- to-json input.pdf objects.json --objects-only
```

This is a strict parser, not a complete implementation of every PDF feature. Encrypted files and damaged cross-reference repair are unsupported. Stream decoding supports Flate (including PNG predictors and 8-bit TIFF prediction), ASCIIHex, ASCII85, and RunLength. Other filters are rejected when decoding is needed. Ordinary image/content stream filters are preserved without decoding. Cross-reference streams must have a direct length. Parsing limits include 100 levels of nesting, one million objects per section, 1,024 cross-reference sections, and 256 MiB per decompressed stream.

From the repository root:

```powershell
dotnet run --project src/PdfObjectKit/PdfObjectKit.Cli -- to-json input.pdf document.json
dotnet run --project src/PdfObjectKit/PdfObjectKit.Cli -- to-pdf document.json rebuilt.pdf
```

Output paths must not already exist unless `--overwrite` is supplied. All commands support it. Parent directories must exist. Commands return exit code 1 on failure. Input is fully processed before opening the output file.

To regenerate an existing JSON file:

```powershell
dotnet run --project src/PdfObjectKit/PdfObjectKit.Cli -- to-json input.pdf document.json --overwrite
```

Overwriting replaces the entire JSON, including any edits. Conversion and a temporary-file write complete before the existing file is replaced. Input and output must use different paths.

To rebuild and replace an existing PDF:

```powershell
dotnet run --project src/PdfObjectKit/PdfObjectKit.Cli -- to-pdf document.json updated.pdf --overwrite
```

A failed conversion leaves the existing output untouched. If a PDF viewer locks the destination, close it and retry.

By default, `to-json input.pdf output.json` writes:

- `output.json`: PDF object graph only.
- `output.texts.json`: text, runs, and stream references under `pages`.
- `output.images.json`: image occurrences and placement data under `pages`.
- `output.pagaraphs.json`: detected paragraphs under `pages`.

`--objects-only` writes only the main JSON. All outputs honor `--overwrite`; an existing derived file also blocks export without it. Rebuilding uses only the main JSON. `PdfObjectDocument` contains only the object graph. Indexes are separate snapshots returned by the extractor APIs.

## Use from C#

Reference `PdfObjectKit/PdfObjectKit.csproj`:

| API | Responsibility |
| --- | --- |
| `PdfReader.Read(bytes)` | Parse and validate a PDF object graph. |
| `PdfWriter.Write(document)` | Rebuild a PDF from its objects; indexes are informational. |
| `PdfTextEditor.UpdateText(document, pageNumber, run, text, textOptions: null)` | Replace one text operand; rebuild external indexes afterward. |
| `PdfConverter.ToJson(document)` | Serialize an object graph to JSON. |
| `PdfConverter.FromJson(json)` | Deserialize and validate an object graph. |

These replace the previous combined object converter. Structural validation is shared internally by the reader, writer, converter, and editor APIs. The object-graph JSON schema is unchanged. The CLI supports `to-json`, `to-pdf`, and `to-markdown`; the legacy text-only conversion and original-file restoration commands have been removed.

```csharp
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using PdfObjectKit.Converters;
using PdfObjectKit.Models;

PdfObjectDocument document = PdfReader.Read(File.ReadAllBytes("input.pdf"));
File.WriteAllText("document.json", PdfConverter.ToJson(document));

// The original PDF is no longer needed.
PdfObjectDocument restored = PdfConverter.FromJson(File.ReadAllText("document.json"));
File.WriteAllBytes("rebuilt.pdf", PdfWriter.Write(restored));
```

`Objects` is the indirect object table. Each object has an object number, generation number and typed value. References remain references, preserving shared resources and page-parent cycles. `Trailer` contains the root reference, document information, IDs and custom entries.

Build independent indexes when needed:

```csharp
using PdfObjectKit.Extractors;

var texts = PdfTextExtractor.Extract(document);
var images = PdfImageExtractor.Extract(document);
```

These are snapshots, not document properties. Changing an index does not modify the object graph. Rebuild snapshots after editing the document.

## JSON representation

`output.texts.json` contains a `pages` array with one text-index entry per page (including blank pages). The index is separate from `PdfObjectDocument` and omitted from the main JSON:

```json
"pages": [
  {
    "pageNumber": 1,
    "text": "Hello world",
    "contentStreams": [{ "objectNumber": 10, "generation": 0 }],
    "formStreams": []
  }
]
```

Search `output.texts.json` for readable text, then use its references to find streams in `objects`. `contentStreams` lists page content streams; `formStreams` lists Form XObject resource candidates, including nested forms and inherited resources. These are page-level navigation references, **not exact text-to-stream mappings**: a content stream may contain only graphics, and a listed form may not be invoked. Reading order and characters depend on font mappings. No OCR is performed; annotation appearances are not separately indexed. A `warning` identifies incomplete indexing without preventing structural export.

### Image index

`output.images.json` contains a `pages` array with one image-index entry per page, including pages without images:

```json
"pages": [
  {
    "pageNumber": 1,
    "images": [
      {
        "imageNumber": 1,
        "isInline": false,
        "pixelWidth": 640,
        "pixelHeight": 480,
        "bitsPerComponent": 8,
        "isImageMask": false,
        "corners": [
          { "x": 20, "y": 260 }, { "x": 60, "y": 260 },
          { "x": 60, "y": 300 }, { "x": 20, "y": 300 }
        ],
        "imageObjects": [{ "objectNumber": 5, "generation": 0 }],
        "encodedDataSha256": "<checksum of encoded image bytes>"
      }
    ]
  }
]
```

The example values are illustrative. `imageNumber` is a one-based occurrence number within the page's image enumeration, not a PDF object number or a stable editing ID. Repeated uses of one resource appear separately, and images inside nested Form XObjects are included. Unused resources and annotation appearance images are not separately indexed.

`corners` contains bottom-left, bottom-right, top-right and top-left in unrotated PDF user-space points, retaining the transformed quadrilateral rather than reducing it to an axis-aligned rectangle. They describe image placement, not its visible clipped area. Coordinate normalization for rotated/cropped pages must be reconciled with any browser viewer before drawing overlays.

`imageObjects` contains the exact indirect image resource invoked by `Do`; encoded image bytes remain in that object's `base64`. These references identify resources, not the exact drawing invocation to delete.

Inline images have no independent image object. They expose `inlineDataBase64`, containing the parser's raw encoded payload, while their original instructions remain in the page content stream. Neither inline bytes nor ordinary image stream bytes are necessarily a standalone PNG/JPEG. Image decoding/export is separate from this index.

The image index is informational: changing it does not edit or delete images in the rebuilt PDF. Re-export to refresh it after editing `objects`. Existing JSON without `imageIndex` remains supported. A page-level `warning` reports partial or failed image indexing without preventing structural export.

### Inserting an image

The `PdfObjectKit.Demo` console project uses QRCoder 1.8.0 to generate a QR code for `https://github.com/phongnguyend`. Put `input.pdf` in the current working directory, then run from the repository root:

```powershell
dotnet run --project src/PdfObjectKit/PdfObjectKit.Demo
```

The demo inserts the QR code on page 1 at `(50, 600)`, with a square size of `100 x 100` points, and writes `output.pdf` in the current directory (replacing it if present). No `logo.png` is needed. QRCoder is a demo-only dependency; the main library and CLI remain dependency-free. The default QR renderer's 1-bit grayscale PNG is supported.

Use `PdfImageEditor.InsertImage` with JPEG or PNG file bytes:

```csharp
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using PdfObjectKit.Converters;
using PdfObjectKit.Models;
using PdfObjectKit.Editors;

var document = PdfReader.Read(File.ReadAllBytes("input.pdf"));

PdfReference imageObject = PdfImageEditor.InsertImage(
    document,
    pageNumber: 1,
    imageBytes: File.ReadAllBytes("logo.png"),
    x: 50,
    y: 600,
    width: 200,
    height: 100);

File.WriteAllBytes("with-image.pdf", PdfWriter.Write(document));
File.WriteAllText("with-image.json", PdfConverter.ToJson(document));
```

Page numbers are one-based. The rectangle uses unrotated PDF user-space coordinates, normally points (72 per inch), with the origin usually at the bottom-left. CropBox, page rotation, UserUnit, and EXIF orientation are not automatically converted. The image is stretched to the supplied width and height and painted after existing page content; annotations can still appear above it. Choose proportional dimensions to preserve its aspect ratio.

The method returns the new image object's reference and updates the supplied document only after success. It isolates the existing graphics state, and creates page-local resources without changing shared resource dictionaries or original content streams. Reacquire object/page references from the document after insertion because its object graph is replaced.

Supported input is 8-bit grayscale or three-component DCT JPEG (baseline, extended sequential, or progressive), and non-interlaced PNG (1-bit grayscale, or 8-bit grayscale, RGB, palette, grayscale-alpha, or RGBA). PNG transparency becomes a PDF soft mask; PDF versions older than 1.4 are upgraded when needed. PNG pixels are decoded and recompressed; JPEG bytes are embedded intact after header validation, without decoding the JPEG entropy data. Images are limited to 16 million pixels. CMYK JPEG, other PNG sample depths, interlaced/animated PNG, and other image formats are rejected. Embedded color profiles and image metadata are not imported; color management is not implemented.

The target page's content must be readable by the native content parser, with balanced `q`/`Q` and `BT`/`ET` operations. Unsupported filters or filtered inline images can prevent insertion. The helper adds content streams around the original sequence. Consequently, the existing text editor's one-content-stream restriction applies afterward: apply text edits before insertion. Rebuilding the page index reports this through `runWarning`. Other pages retain their editable runs where supported. Image insertion is currently a C# API, not a CLI command.

### Detecting paragraphs

`PdfParagraphExtractor.Extract(document, pageNumber)` groups text operands into lines and paragraphs using their geometry. Page numbers are one-based. It reads the actual object streams, so an absent or outdated text index does not affect detection.

```csharp
using PdfObjectKit.Readers;
using PdfObjectKit.Extractors;

var document = PdfReader.Read(File.ReadAllBytes("input.pdf"));
var paragraphs = PdfParagraphExtractor.Extract(document, 1);
foreach (var paragraph in paragraphs)
{
    Console.WriteLine(paragraph.Text);
    foreach (var line in paragraph.Lines)
    {
        foreach (var run in line.Runs)
        {
            Console.WriteLine($"{run.StreamObject}: operator {run.OperatorIndex}, operand {run.OperandIndex}");
        }
    }
}
```

Each paragraph contains lines with baseline coordinates, left/right extents, estimated text height, and source `PdfTextRun` references. Coordinates are in unrotated PDF user space; height is based on the transformed font size, not exact glyph bounds. Line text inserts spaces for visible gaps; paragraph text joins lines with newlines. Detection does not modify or merge PDF objects, and is exported separately by the CLI. For `to-json input.pdf output.json`, the CLI writes the object graph to `output.json` and paragraph data to `output.pagaraphs.json` (filename spelling retained intentionally). The separate file contains `pages`, each with `pageNumber` and `paragraphs`; unsupported pages include `warning` instead, and blank pages have an empty paragraph array. `--objects-only` skips this file. `--overwrite` applies to all outputs. All are prepared before writing, but replacement of the files is not a single filesystem transaction. Paragraphs are informational and are not embedded in the main JSON.

For separate export from C#:

```csharp
using PdfObjectKit.Converters;

File.WriteAllText("output.texts.json", PdfConverter.ToTextsJson(document));
File.WriteAllText("output.images.json", PdfConverter.ToImagesJson(document));
File.WriteAllText("output.pagaraphs.json", PdfConverter.ToParagraphsJson(document));
```


The heuristic groups similar baselines, splits horizontal gaps larger than twice the text height, and joins aligned lines with compatible font sizes and baseline spacing up to 1.6 times their height. It can group multiple operands and content streams. Paragraphs are ordered top-to-bottom then left-to-right, rather than a semantic column reading order. Tables, tight columns, lists, and unusual indentation can require application-specific interpretation.

Only horizontal, unskewed, left-to-right page text is supported. Text in Form XObjects, rotated text, and incomplete text extraction are rejected. OCR, tagged-PDF structure, hyphen joining, and semantic paragraph recognition are not implemented. Run operator indices follow the concatenated page content streams. Retained references identify the source operands; they do not expand the text editor's existing limitations, including its one-content-stream restriction. Reacquire runs after editing.

### Editing text in C#

`textIndex` is inspection data. Changing its page text or run text in JSON does **not** change the rebuilt PDF. `PdfWriter.Write` writes the object graph only. Use `PdfTextEditor` to update a specific text operand:

```csharp
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;

var document = PdfReader.Read(File.ReadAllBytes("input.pdf"));
var run = PdfTextExtractor.Extract(document)[0].Runs!.First(r => r.Text == "Application Form");
PdfTextEditor.UpdateText(document, 1, run, "Application XX Form");
File.WriteAllBytes("updated.pdf", PdfWriter.Write(document));
```

Page numbers are one-based. The selected run identifies a stream object, operator, and string operand, so repeated text elsewhere stays unchanged. The run overload validates the stream checksum and operand identity. Rebuild the text index and reacquire runs after each successful edit. Passing an empty replacement deletes that operand's text.

You can also address the current operand explicitly:

```csharp
PdfTextEditor.UpdateText(document, pageNumber, run.StreamObject,
    run.OperatorIndex, run.OperandIndex, "Replacement");
```

Operator indices are zero-based. Operand index is `-1` for `Tj`, or the `TJ` array index including numeric spacing entries. A heading can span several operands; update each separately using freshly acquired runs.

If the existing font lacks a glyph, pass a TrueType fallback to the editor:

```csharp
PdfTextEditor.UpdateText(document, pageNumber, run, "Replacement",
    new PdfTextEditOptions { FallbackFont = File.ReadAllBytes("font.ttf") });
```

The entire changed operand uses the fallback font when needed. The editor verifies the resulting extracted text before updating the supplied document; failure leaves it unchanged. Rebuild external indexes after a successful edit. JSON serialization preserves the resulting object changes, but editing index fields itself has no effect. The CLI's former `--font` option is removed.

Current constraints:

- One unshared content stream per page and no Form XObject resources. Edit text before inserting images, which adds content streams.
- Vertical fonts, nonzero character/word spacing, quote text operators, inline images, and line-break changes are unsupported for editing.
- Replacements retain font size and following text positions. Wider text can overlap; no automatic shrinking or reflow occurs.
- Runs require reliable font mappings. Unsupported pages report `runWarning` or `warning`. This API is not a secure-redaction tool.

Example stream object inside the `objects` array:

```json
{
  "id": { "objectNumber": 12, "generation": 0 },
  "value": {
    "type": "stream",
    "base64": "AQIDAP8=",
    "entries": {
      "Type": { "type": "name", "name": "EmbeddedFile" }
    }
  }
}
```

Top-level fields: `format: "pdf-object-graph"`, `schemaVersion: 1`, `pdfVersion`, `trailer`, and `objects`.

| PDF type | JSON fields after `type` |
| --- | --- |
| `null` | None |
| `boolean` | `boolean` |
| `number` | `number`, a PDF numeric string without exponent notation |
| `name` | `name`, decoded PDF name bytes mapped to Latin-1 characters |
| `string` | `base64`, actual string bytes including encoding markers |
| `reference` | `reference: { objectNumber, generation }` |
| `array` | `items`, an ordered list of values |
| `dictionary` | `entries`, a map from decoded names to values |
| `stream` | `entries` and `base64`, the original encoded stream bytes |

Literal and hexadecimal strings both become byte strings, avoiding assumptions about text encoding. Name escapes are regenerated. Compression filters and decode parameters are retained; streams are not recompressed. Stream lengths are recalculated from the Base64 data.

## Markdown conversion (first version)

```powershell
dotnet run --project src/PdfObjectKit/PdfObjectKit.Cli -- to-markdown input.pdf output.md --overwrite
```

```csharp
using PdfObjectKit.Converters;
using PdfObjectKit.Readers;

var document = PdfReader.Read(File.ReadAllBytes("input.pdf"));
var result = PdfMarkdownConverter.Convert(document);
File.WriteAllText("output.md", result.Markdown);
foreach (var warning in result.Warnings)
{
    Console.WriteLine(warning);
}
```

The converter first creates `result.Document`, a `PdfStructuredDocument` with pages and source-linked blocks, then renders Markdown. Blocks are paragraphs, headings, list items, or tables. `SourceLines` retain geometry and PDF operand references. `PdfDocumentAnalyzer.Analyze` in `PdfObjectKit.Analysis` exposes this intermediate step directly, and `PdfMarkdownConverter.Render` renders a reviewed or corrected structure. The original PDF graph is unchanged.

This version supports conservative borderless-table detection and horizontal single-column text, wrapped paragraphs, left-aligned or centered multiline headings, and basic flat ordered/unordered lists with hanging continuations. Same-row form labels ending in a colon are joined to their values. Adjacent labeled fields remain separate blocks. Currency fragments are normalized before table detection, and nearby superscripts stay inline. Body text height is estimated using character-weighted frequency in transformed PDF coordinates. Larger or separated bold text becomes a heading candidate; matching nearby lines join into one block. Heading levels follow distinct heading heights across the document. These are heuristics, not tagged-PDF semantics: titles, captions, numbered headings, and unusual styles may need correction. Headers and footers are currently retained.

Use `new PdfMarkdownOptions { BodyTextHeight = 12 }` to override a poor body-height estimate. Hyphens are preserved by default. `JoinHyphenatedWords = true` removes hyphens between alphabetic line endings and lowercase continuations, which can incorrectly join legitimate compound words. Markdown control syntax and HTML are escaped while ordinary punctuation is retained; inline bold/italic, links, and code formatting are not inferred. Page boundaries keep separate blocks; paragraphs are not joined across pages.

Heuristic thresholds are configurable per call. Defaults preserve the existing behavior:

```csharp
using PdfObjectKit.Options;

var heuristics = new PdfHeuristicOptions
{
    HeadingSizeRatio = 1.4,
    MaximumLineGapFactor = 1.8,
    ColumnGutterFactor = 2.5,
    TableRowGapFactor = 4,
    MinimumTableRows = 3,
    EnableSingleRowTables = true
};
var markdown = PdfMarkdownConverter.Convert(document, new PdfMarkdownOptions
{
    Heuristics = heuristics
});
var paragraphs = PdfParagraphExtractor.Extract(document, 1, heuristics);
var paragraphJson = PdfConverter.ToParagraphsJson(document, heuristics);
```

`PdfHeuristicOptions` also controls baseline and superscript grouping, inferred spaces, font-size compatibility, alignment, list indentation, column evidence, and table evidence. Distance factors use transformed text heights, not fixed pixels; each property documents its scale. Standalone paragraphs use `TextColumnGapFactor` (default 2), while Markdown uses `MarkdownColumnGapFactor` (default 0.8) to retain smaller table fragments. `MinimumNumericCells` counts numeric cells below the first row; lowering `MinimumTableRows` may also require lowering this threshold. Styled single-row tables have a separate opt-in rule, enabled by default.

Options are immutable after initialization and validated before processing. Factors must be finite and positive; smaller/larger size ratios must be at most 1. Minimum line gap must be less than maximum line gap. Invalid values throw `ArgumentException` or `ArgumentOutOfRangeException`. CLI commands currently use the defaults; custom thresholds are available through the library API. Adjustments can trade missed structure for false positives, so verify them against representative documents.

Images and vector graphics are not exported as assets in this version. Detected images or unsupported image interpretation produce warnings; image-only pages report the lack of OCR. Rotated/skewed text, text inside Form XObjects, or failed text interpretation causes that page's text to be skipped with a warning. After table detection, `PdfLayoutAnalyzer` separates regions at table boundaries and large vertical gaps. A continuous horizontal gutter wider than twice the typical text height can separate columns when each has at least two lines and their vertical ranges overlap. Resolved regions are read left-to-right, with each column read top-to-bottom. Paragraph joins cannot cross region boundaries. Unresolved parallel fragments remain separate in spatial order and produce warnings containing page number, coordinates, and a text preview. This fallback does not infer column reading order or uncertain table structure; review the output. Other complex layouts may still be misclassified. Complex column layouts (including spanning headings without separating whitespace), ruled-table geometry, automatic merged-cell detection, nested lists, image decoding/assets, OCR, and repeated header/footer removal remain future work.

The CLI writes warnings to stderr and returns success when conversion completes, including partial or empty Markdown. Always inspect `Warnings` when deciding whether the result is usable. `--overwrite` has the same behavior as the other commands; `to-markdown` creates only the Markdown file.

### Image comments

Detected inline images and image XObjects become `PdfContentBlockKind.Image` blocks and render as HTML comments in Markdown:

```markdown
<!-- image: image-<sha256>.jpg -->
<!-- image: logo.png; caption: Company logo; alt: Blue company logo -->
```

The fallback name uses the SHA-256 of the encoded PDF image bytes. Standalone DCT/JPEG streams use `.jpg`; other PDF streams use `.bin` because compressed pixel data is not necessarily a PNG or another standalone image file. No image file is written. Repeated occurrences remain separate blocks with the same content-derived name. `Image.Source` retains pixel dimensions, corners, and object references.

Callers can set `block.Image.FileName`, `Caption`, and `AltText` on the analyzed document before calling `PdfMarkdownConverter.Render`. Captions and alt text are not inferred automatically. The renderer strips directories from filenames and escapes comment delimiters and control characters. A manually created image block needs either a filename or a valid `EncodedDataSha256`; `ContentType` selects the fallback extension.

Image placement uses the top of the image bounds while preserving existing text reading order. When text is to the left of a vertically overlapping image, the comment follows those lines, keeping multiline headings together. Images to the left precede adjacent text; tables stay intact. Complex overlapping or multi-column layouts may need caller adjustment. Images remain available even if page text extraction fails. `ImagesOmitted` continues to report that assets are not exported, and image-only pages still report `OcrRequired`.

### Typed conversion diagnostics

Markdown conversion and document analysis expose `Diagnostics` with stable enum codes in `PdfObjectKit.Diagnostics`. Each `PdfDiagnostic` contains `Code`, `Severity`, `Message`, optional one-based `PageNumber`, related `SourceObjects`, and optional `Location` in unrotated PDF user space. Source objects provide context; they are not always the exact failing object. Diagnostic enums serialize as names in JSON.

```csharp
using PdfObjectKit.Diagnostics;

foreach (var diagnostic in result.Diagnostics)
{
    if (diagnostic.Code == PdfDiagnosticCode.TextExtractionFailed)
    {
        Console.WriteLine($"Review page {diagnostic.PageNumber}: {diagnostic.Message}");
    }
}
```

| Code | Meaning |
| --- | --- |
| `ImagesOmitted` | Image assets were not exported; occurrences are represented by Markdown comments. |
| `ImageExtractionIncomplete` | Image interpretation failed or is incomplete. |
| `OcrRequired` | An image-containing page has no extractable text; OCR is unavailable. |
| `TextExtractionFailed` | Text interpretation failed and the page's text was omitted. |
| `UnsupportedTextLayout` | Extracted text uses a layout the analyzer does not support. |
| `AmbiguousReadingOrder` | Text is preserved, but a region's reading order is uncertain. |

Current recoverable conversion issues have `Warning` severity. `Information` and `Error` are available for diagnostic consumers and future policies. Use codes rather than matching message strings. Existing `Warnings` remains a read-only projection of warning/error messages; change `result.Document.Diagnostics` when annotating a reviewed result. The CLI prints severity and code, for example `Warning [AmbiguousReadingOrder]: ...`.

This first diagnostic API covers Markdown analysis and layout. Low-level parser validation still throws exceptions, and text/image extraction result models retain their existing warning fields. No strict mode or new resource-limit policy is introduced by this change.

### Table blocks

The analyzer detects tables before paragraph/heading classification. It requires at least three aligned rows with at least two occupied cells each (data rows may have empty columns), plus repeated numeric values in a non-first column or a distinct bold header. Both left and right alignment can support a column. Row spacing up to 3.2 times text height is accepted. Header geometry and repeated alignment preserve columns even when no data row fills every cell. Nearby partial rows preserve empty cells; indented single-fragment continuations in the first column join the preceding description cell. A distinct bold header followed closely by one aligned data row is also accepted, including a row with only one populated cell. Empty neighboring cells retain their columns. A lone row without header evidence remains text; geometry from borders is not yet used. These conservative rules can miss text-only tables without a styled header and unindented wrapped cells.

`PdfContentBlockKind.Table` stores a `PdfTable` in `block.Table`. Tables contain `Rows`, each with `IsHeader` and `Cells`. Each cell has `Text`, `RowSpan`, `ColumnSpan`, and `SourceLines` retaining PDF run references. Detected tables consume their source fragments so the same text is not emitted again as paragraphs. The original PDF objects remain unchanged.

Header identification uses a distinct bold style or a transition from text labels to repeated numeric values. It does not automatically designate every first row as a header. Review inferred tables when the layout is ambiguous; `result.Document` can be corrected before calling `PdfMarkdownConverter.Render`.

Rectangular tables with zero or one initial header row use Markdown pipe tables, right-align numeric columns, escape cell text, and render cell line breaks with `<br>`. Headerless tables receive an empty Markdown header, preserving every source row as data. Multiple header rows, ragged rows, and explicit cell spans use escaped HTML `<table>` markup. Positive `RowSpan` and `ColumnSpan` values are supported when supplied by the caller; automatic span detection is not implemented. Table borders are not interpreted, and tables are not joined across page boundaries. Uncertain regions retain the spatial-order text fallback and warnings.

## Reconstruction

Content is carried through as PDF objects: page trees, text operators, fonts and CMaps, images, vector commands, resources, annotations, form dictionaries and appearance streams, attachments, metadata, outlines and other dictionary entries. Features do not require separate rendering implementations because their original objects and stream bytes are retained. JSON edits to those objects are used during reconstruction.

The handwritten parser is `PdfObjectKit/Parsers/PdfNativeParser.cs`. The writer in `Writers/PdfWriter.cs` emits PDF syntax, new byte offsets, a classic cross-reference table and a trailer. Compressed object streams are expanded. Old xref streams, object-stream containers and linearization dictionaries are omitted because they describe the old physical layout. Incremental updates are consolidated into the latest document state.

## Boundaries

- Structural reconstruction is not byte-identical archival storage. Whitespace, dictionary order, numeric spelling, string syntax, xref representation and file size can change.
- Digital signature objects may remain, but signatures become invalid after rewriting their signed byte ranges. Historical revisions and linearization are not preserved.
- Encrypted PDFs are explicitly unsupported. Decrypt before conversion; re-encryption is not implemented.
- Unsupported tokens, dangling references and ambiguous duplicate object numbers fail rather than silently dropping data. Malformed PDFs requiring viewer repair may be rejected.
- Parser support and precision constrain fidelity. Object numbers retain their original decimal token strings; drawing calculations use double precision. Private extensions containing byte offsets may require custom rewriting.
- Editing compressed content requires decoding its filters, editing the data, and updating/re-encoding it and its filter dictionary consistently. Base64 is not text extraction or decompression.
- Validation checks types, IDs, references and nesting, not the semantics of every feature dictionary. Incorrect edits can still break a PDF.
- The entire graph and output are held in memory. Intended for trusted local files; hostile-upload isolation, resource quotas and exhaustive conformance are outside this example.

## Native interpretation limits

Structural preservation does not require rendering or decoding every font or image. Indexing is a separate, narrower feature:

- Text is concatenated in content-stream drawing order. It does not infer spaces, reading order, columns, headings, or OCR text.
- Horizontal simple fonts support ASCII StandardEncoding, WinAnsi, a limited glyph-name mapping for explicit Differences, and ToUnicode `bfchar`/`bfrange`. Fonts require explicit widths, except built-in ASCII Helvetica and Courier metrics. Fonts with unsupported encodings or metrics produce warnings rather than guessed characters/positions.
- Composite fonts require Identity-H and an explicit ToUnicode map with two-byte source codes. Vertical fonts, named/custom encoding CMaps, inherited CMaps, and Type 3 font interpretation are not fully supported.
- Image indexing follows `q`/`Q`, `cm`, and nested Form matrices. Unfiltered inline images support DeviceGray/RGB/CMYK and image masks; filtered inline images are preserved but currently produce indexing warnings. Image pixels are not decoded, and clipping, page rotation, CropBox, and UserUnit are not applied to overlay coordinates.
- TrueType embedding reads Unicode cmap formats 4/12 and horizontal metrics, embeds the complete glyf font program, and writes CIDToGIDMap, widths, and ToUnicode. CFF/OTF outlines, font collections, variable-font instancing, shaping, ligature substitution, and kerning are not implemented. Full embedding can increase file size.
- Unsupported interpretation can leave a partial index with a warning. Unchanged object graphs still rebuild; edits requiring an incomplete text index are rejected.

The implementations follow the [PDF specification resources](https://pdf-issues.pdfa.org/32000-2-2020/clause09.html) and [OpenType cmap specification](https://learn.microsoft.com/en-us/typography/opentype/spec/cmap).

## Tests

```powershell
dotnet build src/PdfObjectKit/PdfObjectKit.Tests
dotnet run --project src/PdfObjectKit/PdfObjectKit.Tests
# Adds embedded TrueType font, CMap and Vietnamese text checks:
dotnet run --project src/PdfObjectKit/PdfObjectKit.Tests -- C:/Windows/Fonts/arial.ttf
```

Independently constructed fixtures cover compressed streams, images, vector drawing, forms, attachments, binary strings, escaped names, nonzero generations, custom trailers, object streams, xref streams and incremental updates. Tests compare reconstructed object data, verify edits, and reject invalid graphs. PDFsharp independently reads rebuilt files. This is not exhaustive compatibility or visual testing of every PDF feature.
