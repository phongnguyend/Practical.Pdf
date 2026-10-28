# PdfObjectKit

C# / .NET 10 library and CLI for reading, editing, and writing PDF object graphs. They load a PDF's current indirect objects into memory, serialize them to JSON, and rebuild a PDF from that JSON. Reconstruction operates at the PDF object level, without re-rendering pages or embedding the entire original file. The CLI writes derived text, image, and paragraph data to separate JSON files by default.

## Docker Compose

From `src/PdfObjectKit`, build and start the playground:

```powershell
docker compose up --build -d
```

Open http://localhost:8074. The UI forwards `/api` requests to the playground
API, which is also available at http://localhost:8070/api/health.
The API and CLI images run as the .NET non-root user. Docker build contexts
exclude local build output and environment files.

To use published images, copy `.env.sample` to `.env` if needed and edit:

```dotenv
DOCKERHUB_NAMESPACE=your-dockerhub-account-or-organization
IMAGE_TAG=latest
```

```powershell
docker compose -f compose.published.yaml up -d
```

Compose loads `.env` automatically. Set `IMAGE_TAG` to a version such as
`1.2.3` to pin a release. Published images target Linux AMD64 and are pulled
on each `up`. Both Compose files use the same project and ports, so switching
between them replaces the existing deployment. Stop it with `docker compose
down` or `docker compose -f compose.published.yaml down`, respectively.

The CLI is optional and does not start with the playground. Put input files in
the `data` folder beside the Compose file; it is mounted at `/data` and ignored
by Git. On Linux, ensure this folder is writable by container UID 1654 when
writing output files.

```powershell
New-Item -ItemType Directory -Force data
# Place input.pdf in data, then use either local or published images:
docker compose run --build --rm cli to-json input.pdf document.json
docker compose -f compose.published.yaml run --rm cli to-pdf document.json rebuilt.pdf
```

### Docker Hub releases

The [PdfObjectKit workflow](../../.github/workflows/pdfobjectkit.yml) builds
the API, CLI, and UI images after the existing library build and snapshot tests.
Branch and pull-request runs build only. Release tags such as
`releases/pdfobjectkit/v1.2.3` also publish:

- `<namespace>/pdfobjectkit-playground-api:1.2.3`
- `<namespace>/pdfobjectkit-cli:1.2.3`
- `<namespace>/pdfobjectkit-playground:1.2.3`

Stable releases publish `latest` from the same images; prereleases publish only
their version tag. Rerunning a stable release republishes both tags, so use the
newest stable release when backfilling `latest`. Configure the repository
Actions secrets `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`, and optionally the
Actions variable `DOCKERHUB_NAMESPACE`, as described in the
[release setup](../../README.md#release-docker-images). Existing NuGet publishing
continues to use `NUGET_API_KEY` on the same release tags.

## Markdown snapshot regression test

The snapshot suite also includes independent extractor tests:

| Test class | Fixture | Captured output |
| --- | --- | --- |
| `TextExtractorSnapshotTests` | `input.pdf` | All four pages of text, runs, fonts, operator indices, and source references |
| `ParagraphExtractorSnapshotTests` | `input.pdf` | Per-page paragraphs, lines, geometry, and source runs |
| `ImageExtractorSnapshotTests` | `images.pdf` | XObjects, repeated and rotated placements, inline data, Form transforms, hashes, and a blank page |

Each class has its own `.verified.txt` baseline. These run with the Markdown and structured-document snapshots in the existing CI step. The synthetic image fixture has a deterministic generator in `Fixtures/generate_images.py`; Python is not required to run the tests.

All snapshots disable Verify's automatic date scrubbing with `DontScrubDateTimes()`. Dates in PDF text are meaningful fixture content and must remain literal. This also avoids platform-dependent date recognition changing `Date_1`/`Date_2` placeholders between Windows and Linux. Geometry and other extracted values are not rounded or ignored.

`PdfObjectKit.SnapshotTests` uses Verify.Xunit to compare the full Markdown generated from `Fixtures/input.pdf` with `Snapshots/MarkdownSnapshotTests.InputPdf.verified.md`. A second test snapshots the full `PdfStructuredDocument` into `StructuredDocumentSnapshotTests.InputPdf.verified.txt`, covering pages, block kinds, tables, source lines/runs, coordinates, and diagnostics without excluding source metadata. The fixture is a copy of the original CLI `bin/input.pdf`, kept with the tests so a clean checkout can run them. The initial baseline captures current behavior; it protects against changes, not every possible extraction error.

```powershell
dotnet test src/PdfObjectKit/PdfObjectKit.SnapshotTests --configuration Release
```

On a mismatch, Verify fails the test and writes a `.received.md` or `.received.txt` file beside the corresponding approved snapshot. Compare it with the `.verified.md` file. For an intentional change, review the entire diff, replace the verified file with the received file, rerun the test, and commit the updated baseline. Do not automatically accept snapshots in CI. Received files are ignored by Git; the PDF fixture and verified Markdown belong in source control. Snapshot Markdown uses LF line endings across platforms.

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

Horizontal, unskewed, left-to-right text is supported in page content and in Form XObjects, including nested Forms and repeated placements. Form matrices are applied to paragraph coordinates. Rotated/skewed text and incomplete text extraction are still rejected. OCR, tagged-PDF structure, hyphen joining, and semantic paragraph recognition are not implemented. Page run operator indices follow the concatenated page content streams; Form run indices are local to their Form stream. Form runs retain the original stream checksum and a one-based `FormOccurrenceId` assigned in page traversal order, including nested invocations. The occurrence ID is null for page text, is scoped to its page, and must be rebuilt after edits. Repeated uses share stream/operator references but have different occurrence IDs. Editing these Form runs is explicitly rejected to avoid changing other placements of shared content. Retained references identify the source operands; they do not expand the text editor's existing limitations, including its one-content-stream restriction. Reacquire runs after editing.

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
- Nonzero character/word spacing is supported: edits preserve the active spacing and compensate the changed advance so following text stays in place. Word spacing follows PDF character codes (single-byte code 32), including when a fallback font is used. Changes requiring spacing compensation at zero font size are rejected.
- Vertical fonts, quote text operators, inline images, and line-break changes are unsupported for editing.
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

Images and vector graphics are not exported as assets in this version. Detected images or unsupported image interpretation produce warnings; image-only pages report the lack of OCR. Rotated/skewed text or failed text interpretation causes that page's text to be skipped with a warning. After table detection, `PdfLayoutAnalyzer` separates regions at table boundaries and large vertical gaps. A continuous horizontal gutter wider than twice the typical text height can separate columns when each has at least two lines and their vertical ranges overlap. Resolved regions are read left-to-right, with each column read top-to-bottom. Paragraph joins cannot cross region boundaries. Unresolved parallel fragments remain separate in spatial order and produce warnings containing page number, coordinates, and a text preview. This fallback does not infer column reading order or uncertain table structure; review the output. Other complex layouts may still be misclassified. Complex column layouts (including spanning headings without separating whitespace), ruled-table geometry, automatic merged-cell detection, nested lists, image decoding/assets, OCR, and repeated header/footer removal remain future work.

The CLI writes warnings to stderr and returns success when conversion completes, including partial or empty Markdown. Always inspect `Warnings` when deciding whether the result is usable. `--overwrite` has the same behavior as the other commands; `to-markdown` creates only the Markdown file.

### Image metadata blocks

`PdfMarkdownOptions.ImageRender` lets clients choose how image blocks render. The default delegate is `DefaultMarkDownImageRender.Render`, which retains the labeled JSON format below. Both `Convert` and `Render` accept `PdfMarkdownOptions` for customization:

```csharp
var options = new PdfMarkdownOptions
{
    ImageRender = (image, pageNumber, imageNumber) =>
        DefaultMarkDownImageRender.Render(image, pageNumber, imageNumber)
};
var result = PdfMarkdownConverter.Convert(document, options);

// After enriching result.Document with OCR or AI output:
var markdown = PdfMarkdownConverter.Render(result.Document, new PdfMarkdownOptions
{
    ImageRender = (image, pageNumber, imageNumber) =>
        DefaultMarkDownImageRender.Render(image, pageNumber, imageNumber)
});
```

Replace the delegate body with your own Markdown format. It receives the enriched image and its one-based page/occurrence numbers. Return an empty string to omit an image. Custom output is emitted verbatim, so the client must escape it appropriately; the converter handles separators between blocks. Pass the options again when re-rendering an enriched document.

Each detected image occurrence renders as a fenced JSON block in reading order:

````markdown
**Image content (page 1, image 1):**

```json
{
  "type": "image",
  "fileName": "logo.png",
  "pageNumber": 1,
  "imageNumber": 1,
  "contentType": "image/png",
  "ocrText": "CONTOSO\nInvoice 123",
  "imageUnderstandingText": "A company invoice."
}
```
````

Clients can populate `FileName`, `Caption`, `AltText`, `OcrText`, and `ImageUnderstandingText` before calling `PdfMarkdownConverter.Render`. Empty enrichment fields are omitted. JSON serialization preserves multiline text and escapes quotes and control characters. The fence is longer than any backtick run in the serialized payload, keeping image information separate from surrounding prose.

Filenames use the supplied basename or `image-<sha256>` derived from encoded PDF bytes. DCT/JPEG streams use `.jpg`; other encoded streams default to `.bin`. No image files are exported. `pageNumber` comes from the containing page; `imageNumber` uses the source occurrence number, or the image's one-based position on the page for manually created blocks. Repeated images produce separate blocks. Source geometry remains available in `Image.Source` but is not embedded in Markdown.

Text to the left of a vertically overlapping image precedes its metadata block. Tables stay intact. Complex layouts may need caller adjustment. Images survive text extraction failures; `ImagesOmitted` reports missing asset export, and image-only pages still report `OcrRequired`.

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
| `ImagesOmitted` | Image assets were not exported; occurrences are represented by fenced JSON blocks. |
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

## AcroForm fields

Rename an existing terminal field with `PdfFormEditor.RenameField(document, "Applicant.FullName", "LegalName")`. The result is `Applicant.LegalName`: the new name replaces only the final partial name, retaining the parent group, values, widget references and appearance streams. Unicode names are supported. Empty names, dots, control characters, name collisions and ambiguous targets are rejected atomically. Read-only fields, signed forms, XFA forms, and documents containing JavaScript or name-dependent form actions are unsupported for renaming; scripts and action name references are not rewritten.

In the playground, select a form field, edit **Field name**, and click **Rename**. The API accepts `kind: "rename-field"`, the current full `fieldName`, and the new partial name in `value` through the existing edit endpoint. The rebuilt PDF and overlays refresh using the same workflow as value edits.

Read existing interactive fields with `PdfFormExtractor` and fill them with `PdfFormEditor`. These are AcroForm fields, distinct from reusable Form XObjects.

```csharp
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;

var document = PdfReader.Read(File.ReadAllBytes("application.pdf"));
var fields = PdfFormExtractor.Extract(document);

PdfFormEditor.SetValue(document, "Applicant.FullName", "Jane Smith");
PdfFormEditor.SetValue(document, "Applicant.FullName", "Jane Smith", PdfTextAlignment.Center);
PdfFormEditor.SetValue(document, "Country", "US"); // Dropdown export value.
PdfFormEditor.SetChecked(document, "Accepted", true);
PdfFormEditor.SetValue(document, "Plan", "Premium"); // Radio appearance-state name.

File.WriteAllBytes("filled.pdf", PdfWriter.Write(document));
```

Create additional field types with generated normal appearances:

```csharp
PdfFormEditor.InsertCheckBox(document, 1, "Accepted", false, 40, 600, 20, 20);
PdfFormEditor.InsertDropdown(document, 1, "Country",
    [new("US", "United States"), new("UK", "United Kingdom")], "US", 40, 550, 180, 24);
PdfFormEditor.InsertRadioGroup(document, 1, "ContactMethod",
    [new("Email", "By email"), new("Phone", "By phone")], "Email", 40, 470, 180, 48);
```

In the playground, expand **Form field** in the Create objects toolbox, then drag or click **Text field**, **Rich text**, **File select**, **Password**, **Comb**, **Multiline text**, **Checkbox**, **Dropdown**, **List box**, **Multi-select list box**, **Radio group**, **Pushbutton**, or **Signature field**. The Inspector shows only the settings for the chosen type; there is no field-type dropdown. Collapse the group to save space. Dropdowns, list boxes and radio groups accept one option label per line and an initial selection. Radio options occupy equal-height rows; allow at least 16 PDF points per option. Radio export states in the playground are `Choice1`, `Choice2`, etc.; the library accepts custom export states. `Off` is reserved for an unchecked/cleared button. Names must be unique top-level names. New dropdowns are noneditable and single-select. Labels currently use printable ASCII; checkbox/radio appearances are generated as vector graphics. Placement previews update live. Both renderers display saved normal widget appearances. The native renderer reports widgets with missing appearances instead of synthesizing their appearance from values.

Extraction returns terminal fields with qualified names, types, inherited flags and values, default values, choice export/display pairs, and widget references, page numbers, rectangles and appearance-state names. Documents without an AcroForm return an empty list. XFA forms are unsupported.

Editing updates the field value and every associated widget appearance. Checkboxes and radio buttons use their existing appearance streams; `SetValue` accepts an appearance-state name or `Off`. Single-line/multiline text, single-selection dropdowns, and single/multi-select list boxes generate plain black 12pt Helvetica appearances using printable ASCII display text. This replaces custom styling, borders and backgrounds, and clips text to the widget rectangle without fitting. Text and dropdown fields support `PdfTextAlignment.Left`, `Center`, and `Right`. Pass the optional `textAlignment` argument to `SetValue` or `InsertTextField`; value edits preserve the inherited alignment when it is omitted. `PdfFormExtractor` exposes `TextAlignment`. The playground provides alignment buttons for new and existing fields, with live alignment previews when creating fields. Dropdowns store the export value and display the matching label. Fields remain interactive; they are not flattened.

Edits are atomic: rejected operations leave the document unchanged. Read-only fields, signed forms, calculation-order forms, target fields/widgets with additional actions, unsupported button appearances, rotated widgets, unsupported rich-text markup and unsupported display characters are rejected. First RSA approval signatures and common button actions are supported as described below. Script execution and arbitrary font/style preservation are not implemented. Pushbutton captions can be edited using `SetPushButtonCaption`. `PdfFormEditor.InsertTextField` creates a top-level text field (`multiline: true` enables line breaks and wrapping); `DeleteField` removes a terminal field and all its widgets from the field tree and page annotations.

## Browser playground

`PdfObjectKit.Playground.Api` is a .NET 10 ASP.NET Core Minimal API. `PdfObjectKit.Playground.Ui` is a React 19 / TypeScript / Vite application. The API extracts text-line bounds, image placements and AcroForm widget rectangles; PDF.js renders the original PDF to canvas in the browser. A second canvas draws selectable overlays using the same viewport transformation, including crop and rotation. The inspector shows source references and metadata. Text bounds are approximate; text lines may contain multiple source runs. Vector paths and other raw PDF objects are rendered by PDF.js but are not individually selectable.

Run the API from the repository root:

```powershell
dotnet run --project src/PdfObjectKit/PdfObjectKit.Playground.Api
```

In another terminal (Node.js 22.13+ or a newer supported LTS):

```powershell
cd src/PdfObjectKit/PdfObjectKit.Playground.Ui
npm ci
npm run dev
```

Use the **PDF.js / PdfObjectKit (experimental)** buttons in the top toolbar to compare renderers without changing the document, page, zoom, selection, or edits. PDF.js remains the default. In native mode, `POST /api/documents/render?page=1` uses `PdfCanvasExporter.Export(document, pageNumber)` to produce a display list from our parser, and `src/renderers/nativeRenderer.ts` paints it with Canvas 2D. It does not call PDF.js page rendering. PDF.js still loads document/page geometry for the shared viewport and serves as the reference renderer.

The experimental renderer supports decoded horizontal text with PDF glyph advances, basic paths, clipping, transforms, RGB/gray/CMYK drawing colors, axial RGB shading patterns with exponential/stitching functions, Form XObjects, JPEG RGB/gray images, and 8-bit RGB/gray image streams with simple soft masks. Edits are rebuilt and painted with the selected renderer. Rendering notes identify unsupported operators and image formats. Embedded glyf TrueType fonts (`FontFile2`) now load through the browser FontFace API. The exporter rebuilds a private-use cmap for the used glyphs: Identity-H CID fonts use CIDToGIDMap (or identity), while supported simple fonts use their PDF encoding and TrueType cmap. ToUnicode remains extraction metadata and does not select the rendered CID glyph. PDF advances position each glyph without stretching its embedded outline. Non-embedded fonts, unsupported formats/mappings, malformed fonts, and browser-rejected font data use browser substitutes with specific rendering notes. Type1/CFF/Type3 and vertical-font rendering are not implemented. Browser rasterization can still differ from PDF.js. Font faces are released after each canvas render. Gradient functions are sampled at 257 stops; sharp discontinuities and nonlinear transitions are approximate. Non-widget annotations, widgets without saved appearances, text clipping, tiling patterns, other shading types, advanced color spaces, blend modes and transparency groups are not fully rendered. Use PDF.js to check fidelity; this is a starting point for incremental renderer development, not a full PDF renderer. Display lists are bounded to 200,000 commands and 16 million decoded image pixels per page; these do not replace parser/decompression security limits.

Press **Delete** to remove the selected image occurrence, whole text line (all its runs), or form field, then automatically save and rerender. The shortcut is inactive while editing inputs, while a dialog is open, during document operations, and for read-only fields. Holding the key does not repeat deletion. Form-field deletion follows the Inspector action and removes all widgets of that field.

Select an image and choose **Download image** in the Inspector to save its full source pixels at their original resolution. Plain supported JPEGs are preserved byte-for-byte; decoded images and JPEGs with transparency export as PNG with their soft mask and matte correction. This works with either renderer and exports the current saved PDF. Page clipping, placement rotation and other page content are excluded. Unsupported image encodings show an export error instead of downloading incomplete data.

Open `http://localhost:5173`, upload or drop a PDF, choose a page, and select an outline or an entry in the object list. Toggle text/image/field layers and adjust zoom. The UI proxies `/api` to `http://localhost:5080`; keep both processes running. PDF.js workers, fonts, CMaps and WASM assets are served locally, with no runtime CDN dependency.

The playground supports text-run editing and AcroForm filling. Select a text line, choose its source run, enter a new value and click Save. For fields, enter a text/dropdown export value or select a checkbox/radio appearance state. Existing library restrictions apply (including unsupported shared Form text, missing glyphs and unsupported field styles). Text field appearances are regenerated as plain 12pt Helvetica. Drag Text, Image, or a specific type from the expandable Form field group in the left Create objects toolbox onto the canvas. A placement box appears at the drop position: drag its body to move it and its corner handles to resize width and height. Enter text or choose an image (up to 5 MB) in the inspector, then choose Insert object to commit. Clicking a toolbox item or Add object is a keyboard-accessible alternative. Drawing a fresh rectangle on an empty part of the page is also supported. Dropping a supported raster image onto an open document starts image placement with a preview. The playground checks file signatures before upload: PNG/JPEG bytes are preserved, while other browser-decodable raster formats (WebP, GIF, BMP and AVIF) are converted to PNG. Animated formats use one frame. Converted PNGs must also fit the 5 MB limit and 16-million-pixel limit. Renaming a file does not convert its format; unreadable images produce a client-side error without modifying the PDF. The library/API still accept only PNG/JPEG bytes. Numeric coordinates remain available for keyboard use. Image/field dimensions follow the rectangle; resizing a text placement changes its font size from the rectangle height without wrapping or fitting its width. The lower PDF-space edge is the text baseline. Text insertion uses black ASCII Helvetica. Cancel discards a pending insertion.

The inspector provides Delete text run, Delete image occurrence, and Delete field. Field deletion removes all widgets for that field across the document. Image deletion preserves other placements, including shared nested Forms, by cloning the changed invocation path. Vector-path editing and arbitrary raw-object deletion are not exposed. Deleted content can remain in unreachable/shared PDF objects; these operations are not secure redaction.

Each Save posts the current PDF and one edit to the API, rebuilds it, refreshes extraction, and renders with the selected renderer while retaining page and zoom. The previous canvas remains visible until its replacement is ready. Rejected edits retain the previous PDF and show an error. Download PDF returns the latest successfully loaded bytes; unsaved input is not included. The API has no document sessions or persistent file storage: uploaded bytes are processed in memory and discarded after each operation. It accepts up to 20 MB and 100 pages, and processes one inspection at a time. Extraction notes are displayed when features cannot be interpreted. This is a local development tool for trusted PDFs, not a hardened public upload service: parser/decompression resource quotas, authentication and execution isolation are not implemented. The page limit is checked after image extraction. Binding the API to a public interface requires additional safeguards.

The upload endpoint is `POST /api/documents/inspect` with raw PDF bytes and `Content-Type: application/pdf`. It returns `pdfVersion`, `pageCount`, `objectCount`, `objects` (page, kind, label, corners and details) and `warnings`. Corners use unrotated PDF user-space coordinates. Invalid or unsupported PDFs return Problem Details; concurrent requests return 429. `POST /api/documents/edit` accepts multipart fields `file` (the current PDF) and `edit` (JSON containing `kind`, `value`, `pageNumber`, and either `run` or `fieldName`). Additional commands are `add-text`, `add-image`, `add-field`, `delete-image` and `delete-field`. Insertions accept PDF-space `x`, `y`, `width`, `height` and `fontSize`; image insertion also accepts an `image` multipart file. Image deletion uses the current `imageNumber` and page number. The response is the rebuilt PDF. Text run checksums are validated by the library; always reacquire targets after editing. `GET /api/health` is available for startup checks.

Validation commands:

```powershell
dotnet build src/PdfObjectKit/PdfObjectKit.Playground.Api
cd src/PdfObjectKit/PdfObjectKit.Playground.Ui
npm run build
npm test
npm run test:e2e
```

Browser tests use installed Google Chrome and start the API and Vite if needed. They cover real fixture uploads, selection, document replacement, page navigation, zoom, invalid uploads, form overlay alignment on rotated/cropped pages, text and form saves, rejected edits, downloaded PDF contents, text/image/field insertion and deletion, and drag/drop/draw/move/resize placement on a rotated page. Set `PLAYGROUND_API_URL`, `PLAYGROUND_UI_PORT` and optionally `PLAYGROUND_CONFIGURATION` to run tests on alternate ports without interrupting existing development servers. `npm run build` creates `dist`; production hosting needs a same-origin `/api` reverse proxy. `npm run preview` only previews static UI assets and does not provide that API proxy.

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

The playground toolbar includes **View Markdown** for the current saved PDF. The viewer uses Marked with sanitized HTML, provides Preview and Source views, conversion notes, and a `.md` download. Uninserted drafts are not included.

Playground UI layout: `src/pages/PlaygroundPage.tsx` owns the page state; `src/components/` contains reusable controls, shared `Modal` and `Toast`, and colocated dialog styles. `src/main.tsx` only mounts the page. Copy `.env.example` to `.env` to configure `PLAYGROUND_API_URL` and `PLAYGROUND_UI_PORT` for Vite; process environment variables take precedence. Local `.env` files are ignored by Git.

Select an image in the playground and use **Replace image** to upload or draw a replacement. **Fit** (default) preserves aspect ratio with transparent margins, **Fill** centers and crops to the existing placement, and **Stretch** uses the full placement. Only the selected occurrence changes, including occurrences inside shared Forms. The API is `PdfImageEditor.ReplaceImage(document, pageNumber, imageNumber, imageBytes, PdfImageFit.Fit)`. Re-extract image indexes after replacement; occurrence numbers refer to the current document. Replacement preserves the original transform, clipping and drawing order; unused source objects remain in the PDF, so this is not secure redaction.

The image picker also offers **Generate QR code** for a URL or text. Generation and preview run locally in the browser; **Use QR code** creates a PNG for insertion or replacement. New image placements become square. For replacements, use Fit to preserve the QR aspect ratio and border.

In the playground, select an existing image or editable form field, then drag its outline to move it. Releasing saves the new position and refreshes the PDF automatically; Escape cancels the drag. Image dimensions and field values are preserved. Image-local clipping and single-image Form boundaries move with the image. Shared ancestor clipping that also applies to other artwork remains fixed.

Select a text segment in the playground and drag its outline to move all its runs together. Drop saves automatically; Escape cancels. Fonts, sizes, spacing, and other text placements are preserved. The library API is `PdfTextEditor.MoveText(document, pageNumber, runs, deltaX, deltaY)` with deltas in unrotated PDF page coordinates. Re-extract runs after edits. Shared Form occurrences are edited independently. Clipping inside groups containing only the selected text moves with that text, including exclusive Form bounding boxes. Clipping shared with unselected artwork remains fixed; text used as a clipping path is rejected.

To remove a selected text line in the playground, choose **Delete whole line**. It removes every indexed run in that line and saves automatically; **Delete text run** still removes only the selected segment. Library clients can call `PdfTextEditor.DeleteText(document, pageNumber, runs)`. Deletion preserves subsequent text positions and other shared Form placements. This is content editing, not secure redaction.

### Multiline text and list boxes

```csharp
PdfFormEditor.InsertTextField(document, 1, "Notes", "First line\nSecond line",
    40, 400, 240, 80, multiline: true);
PdfFormEditor.InsertListBox(document, 1, "Country",
    [new("US", "United States"), new("UK", "United Kingdom")],
    "UK", 40, 300, 240, 60);
PdfFormEditor.SetValue(document, "Notes", "Updated\nnotes");
PdfFormEditor.SetValue(document, "Country", "US");
```

Multiline fields preserve the complete value, wrap at spaces (or characters for long words), and draw top-down with 14pt line spacing. Overflow is clipped to the widget; font size is not reduced. CR, LF and CRLF line endings are accepted. List boxes store the export value and selection index, highlight the selected label, and scroll the saved appearance to that option. An empty value clears the selection. Both support left/center/right alignment. The playground offers live approximate previews and editing for these modes. Rich-text and file-select modes use the dedicated APIs described below.


Multi-select list boxes use an array of export values:

```csharp
PdfFormEditor.InsertMultiSelectListBox(document, 1, "Languages",
    [new("en", "English"), new("fr", "French"), new("de", "German")],
    ["en", "de"], 40, 200, 240, 80);
PdfFormEditor.SetValues(document, "Languages", ["fr", "de"]);
PdfFormEditor.SetValues(document, "Languages", []); // Clear all selections.
```

`SetValues` requires a multi-select list box. It validates unique export values, stores values and selection indices in option order, and regenerates all widget appearances with highlighted selections. Invalid or ambiguous selections leave the document unchanged. The saved appearance begins at the first selected option and clips overflow; interactive viewers can scroll through the remaining options. The playground offers checkboxes for initial selections and editing, including clearing all selections and resetting unsaved edits. Use `SetValue` for single-selection fields.


### Pushbuttons and unsigned signature fields

```csharp
PdfFormEditor.InsertPushButton(document, 1, "ContinueButton", "Continue", 40, 120, 140, 30);
PdfFormEditor.SetPushButtonCaption(document, "ContinueButton", "Next step");
PdfFormEditor.InsertSignatureField(document, 1, "ApproverSignature", 40, 60, 240, 50);
```

Pushbuttons have a centered printable-ASCII caption and a generated 12pt Helvetica appearance. `SetPushButtonCaption` updates all widgets and replaces custom appearance styling; it does not set a field value or execute actions. The extractor exposes the normal caption as `PdfFormWidget.Caption`. Newly created buttons have no actions attached.

Signature fields are unsigned placeholders with no `/V` signature dictionary. Their appearance reads ?Unsigned signature?; this is not a digital signature or a drawn signature image. Sign them using `PdfSigner.Sign` or another signing tool. Creation and caption editing reject already signed forms. The playground supports placement previews, caption editing, and the existing move/rename/delete actions for these fields. Use `PdfSigner.Sign` or the playground signing controls to create a first digital signature, and `SetButtonAction` to configure an action. `Unknown` is an extraction fallback, not a creatable field type.


The native renderer draws visible widgets after page content using their saved normal `/AP /N` appearances. Checkbox/radio state dictionaries use `/AS`. Appearance matrices and bounding boxes map into each widget rectangle; hidden, invisible and NoView widgets are skipped. Rendering does not modify the PDF or add widget text to the text extractor. Widgets without usable appearances produce a rendering note; regenerate appearances by editing the field or use a PDF viewer that synthesizes them. NoZoom/NoRotate annotation flags and other advanced annotation behavior remain unsupported.


### Password and comb text fields

```csharp
PdfFormEditor.InsertTextField(document, 1, "Password", "sample", 40, 400, 180, 30,
    maxLength: 12, password: true);
PdfFormEditor.InsertTextField(document, 1, "Code", "AB12", 40, 350, 180, 30,
    maxLength: 6, comb: true);
PdfFormEditor.SetValue(document, "Code", "CD34", PdfTextAlignment.Center);
```

`maxLength` is optional for regular, multiline and password text fields, and required for comb fields. It must be a positive integer; creation and edits reject values exceeding it without changing the document. `PdfFormField.MaxLength` exposes the inherited limit. Comb appearances place one character in each equal-width cell, with left/center/right cell alignment and clipping per cell. Comb cannot combine with password or multiline. Password fields are single-line and display asterisks in saved appearances, the placement preview, and masked inspector controls. **Masking is not encryption:** the actual value is stored in the PDF and remains available to extraction clients. These modes currently accept printable ASCII. The playground provides the two modes and character-limit controls; both PDF.js and the native renderer use their saved appearances. See below for rich text, file-select fields, digital signing and button actions.


### Rich text and file-select fields

```csharp
PdfFormEditor.InsertRichTextField(document, 1, "Message",
    "<body><p>Hello <b>world</b></p></body>", 40, 400, 240, 80);
PdfFormEditor.SetRichText(document, "Message",
    "<body><p><span style=\"font-size:18pt;color:#ff0000\">Updated</span></p></body>");
PdfFormEditor.InsertTextField(document, 1, "AttachmentPath", "report.txt",
    40, 350, 240, 30, fileSelect: true);
```

Rich text normalizes the XHTML namespace and stores synchronized XHTML (`RV`) and plain text (`V`) and generates styled appearances for both renderers. Supported elements: `body`, `p`, `span`, `b`/`strong`, `i`/`em`, `u`, `br`. Supported styles: Helvetica/sans-serif, 1-200pt sizes, bold/normal weight, italic/normal style, underline/none, and `#RRGGBB` colors. Text is ASCII, wraps at character boundaries and clips overflow. Unsupported elements/styles, DTDs and external entities are rejected; this is not an HTML/CSS browser. Use `SetRichText`, not `SetValue`, for rich fields. `PdfFormField.RichText` exposes stored markup. The playground offers an XHTML source editor and an approximate plain-text placement preview; saved appearances carry styling.

File-select fields set the PDF FileSelect flag and store a path/name as a single-line string. The playground file picker supplies only a file name: it does not upload, attach, or read the file contents. File-select cannot combine with password, comb or multiline flags. Supporting PDF viewers decide how their own file picker behaves.

### Pushbutton actions

```csharp
PdfFormEditor.SetButtonAction(document, "ContinueButton",
    new(PdfButtonActionKind.OpenUrl) { Url = "https://example.com/" });
PdfFormEditor.SetButtonAction(document, "ContinueButton",
    new(PdfButtonActionKind.GoToPage) { PageNumber = 2 });
PdfFormEditor.SetButtonAction(document, "ContinueButton",
    new(PdfButtonActionKind.ResetForm) { FieldNames = ["Message"] });
PdfFormEditor.SetButtonAction(document, "ContinueButton",
    new(PdfButtonActionKind.SubmitForm) { Url = "https://example.com/submit" });
PdfFormEditor.SetButtonAction(document, "ContinueButton", null); // Remove the primary action.
```

Actions are stored in widget `A` dictionaries and exposed through `PdfFormWidget.Action`. Reset/submit can target named fields; omit `FieldNames` for all fields. Submit uses XFDF. URLs must be HTTP(S) without embedded credentials. Actions are never executed by the library or playground; use a supporting PDF viewer. JavaScript, launch actions and action chains are not generated. Viewer support and security settings determine behavior.

### Digital signatures

```csharp
using PdfObjectKit.Signing;
using System.Security.Cryptography.X509Certificates;

using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
    "signer.pfx", certificatePassword, X509KeyStorageFlags.EphemeralKeySet);
byte[] signedPdf = PdfSigner.Sign(document, "ApproverSignature", certificate, "Approved");
File.WriteAllBytes("signed.pdf", signedPdf);
```

Signing creates a first approval signature using RSA (2048+ bits), SHA-256 and detached CMS (`adbe.pkcs7.detached`). The core library remains dependency-free; tests independently verify the CMS using .NET `SignedCms`, including tamper detection. The signer requires a writable unsigned signature field, a current certificate and an accessible RSA private key. It returns final bytes without modifying the source graph. **Save these exact bytes:** rebuilding or editing them invalidates the signature.

The playground accepts a PFX/P12 up to 1 MB and its password for the current local API operation, loads the key ephemerally, and does not persist the certificate. Finish edits before choosing **Sign PDF**. The API rejects further editing of signed PDFs. `PdfFormField.IsSigned` means a signature value is present; it is not a trust or integrity verdict. Validate the output with a PDF signature validator. A self-signed test certificate will not automatically be trusted by viewers.

Limitations: one signature on a rewritten unsigned document; no incremental/countersigning, certification/DocMDP, seed-value constraints, ECDSA, timestamps, revocation evidence, long-term validation, or PAdES conformance claim. Only the signer certificate is included. Signing does not establish certificate-chain trust. These unsupported cases are not silently represented as completed signing workflows.

Format references: [Adobe signature formats](https://www.adobe.com/devnet-docs/acrobatetk/tools/DigSigDC/standards.html), [.NET detached CMS](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.pkcs.signedcms.computesignature).

### Selecting and deleting multiple playground objects

Click an outline or object-list entry to select one object. Hold Ctrl (Cmd on macOS) while clicking to add or remove objects from the selection. Use **Delete selected objects** in the inspector or press **Delete** outside an input to delete the selection in one operation. A normal click selects just that object; changing pages, layers, or starting object creation clears the selection. Multiple objects can be selected on the current page.

Bulk deletion supports text lines, image occurrences, and form fields. Selecting several widgets of one field deletes that field once, including its other widgets. Read-only fields cannot be deleted. If any operation fails, the original PDF and selection are preserved. Editing and dragging remain single-object operations.

### Extracting, splitting and merging pages

These library APIs return new object graphs and do not modify their inputs:

```csharp
using PdfObjectKit.Editors;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;

var source = PdfReader.Read(File.ReadAllBytes("input.pdf"));

// One-based, distinct page numbers; output follows the requested order.
var extracted = PdfPageEditor.ExtractPages(source, [3, 1]);
File.WriteAllBytes("selected.pdf", PdfWriter.Write(extracted));

// One independent PDF per page.
var split = PdfPageEditor.Split(source);
for (var index = 0; index < split.Count; index++)
{
    File.WriteAllBytes($"page-{index + 1}.pdf", PdfWriter.Write(split[index]));
}

// Input order is preserved. Object numbers are remapped per source.
var second = PdfReader.Read(File.ReadAllBytes("second.pdf"));
var merged = PdfDocumentMerger.Merge([source, second]);
File.WriteAllBytes("merged.pdf", PdfWriter.Write(merged));
```

The copier retains reachable page content, fonts, images, nested Form XObjects,
ordinary annotations, and inherited Resources, MediaBox, CropBox and Rotate.
Streams are copied without decoding/recompressing. Explicit links between retained
pages are remapped. A link to an excluded page is rejected rather than left broken.
Duplicate page numbers within one extraction and empty selections are rejected.

Page assembly also supports:
- AcroForm fields and widget appearances, filtered to retained pages. Fields remain editable; default appearance resources are renamed per source to avoid font-resource collisions.
- Bookmarks, with excluded-page bookmarks pruned and surviving children retained. Named destinations used by links/bookmarks are resolved to explicit page destinations. Retained named destinations are also exported in a sorted name tree.
- Page labels, including reordered pages and source-relative numbering.
- Optional-content groups with default visibility, automatic usage applications scoped to their source groups, ordering, radio groups and locked groups (View intent).
- Embedded-file name trees, associated files and file-attachment annotations.
- First-input metadata, document information, language, viewer preferences, page presentation settings and opening destination/action. Output intents are retained when all merged inputs have identical resolved intent dictionaries and profile bytes.

The first input supplies document-wide metadata/settings; later inputs do not overwrite them. Duplicate root field names, named destinations and attachment names are rejected by default. Explicit options below support namespacing/renaming; names are never silently overwritten. Links/actions targeting excluded pages are rejected, while bookmarks targeting those pages are pruned.

**This is not full PDF-specification coverage.** Signed/certified documents, encryption, XFA forms, incompatible output intents, article threads, portfolios, catalog additional actions, JavaScript/other unsupported name trees, and alternate/Unchanged-state/non-View layer configurations remain explicitly unsupported. Signatures cannot remain valid across rewritten document bytes. Supported copying does not certify PDF/A, PDF/UA, or signature trust. Annotation and field actions are copied as data and are not executed.

The APIs load graphs into memory; Split creates all single-page graphs at once.
For large documents, call ExtractPages one page at a time and write each result
before continuing.

### Assembly limits, cancellation and conflict policies

```csharp
using PdfObjectKit.Options;

var options = new PdfPageAssemblyOptions
{
    MaxPages = 2000,
    MaxObjects = 250000,
    MaxNodes = 1000000,
    MaxEncodedStreamBytes = 256L * 1024 * 1024,
    NamespaceFormFields = true,
    RenameConflictingAttachments = true,
    RenameConflictingDestinations = true
};

var combined = PdfDocumentMerger.Merge(documents, options, cancellationToken);

// Lazy splitting avoids keeping all output graphs in memory at once.
foreach (var page in PdfPageEditor.SplitPages(source, options, cancellationToken))
{
    // Write/process this page before requesting the next one.
    var bytes = PdfWriter.Write(page);
}
```

All ExtractPages, Split, SplitPages and Merge APIs accept optional options and a
CancellationToken. MaxPages limits the result. Object, node and encoded-stream-byte
limits check each input and the assembled output; split limits apply separately
to each returned single-page document. They are not a process-memory ceiling or
limits on the separate PDF parsing step. Cancellation is cooperative.

NamespaceFormFields prefixes every root name with Document1_, Document2_, etc.,
and rewrites string field references in action /Fields arrays. It rejects inputs
containing JavaScript because script field references cannot safely be rewritten.
Partial extraction that removes form fields also rejects JavaScript dependencies.
Attachment/destination collision renaming adds a source and collision-counter
prefix. File specifications and embedded file bytes remain unchanged.

The copier validates malformed name/number trees and rejects catalog entries
without an explicit preservation policy instead of silently dropping them.
Later input opening actions and document-wide metadata do not replace those of
the first input. Output-intent compatibility is deliberately conservative: it
compares resolved objects and encoded profile bytes, not colorimetric equivalence.
A missing intent on one input conflicts with an intent on another.

Regression checks cover conflict resolution, editable fields, action references,
named destinations, output intents, automatic layer scoping, cancellation, limits,
source immutability and independent PDFsharp reads. This is progress toward
production readiness, not a blanket PDF/A, PDF/UA, rendering-fidelity or security
certification. XFA, portfolios and signatures remain explicit
limits; a broader real-world corpus and external conformance validation are still
needed before declaring unrestricted production support.

### Playground document tabs

The playground uses browser-style document tabs with titles, close controls and a
new-tab button. Open PDF accepts multiple files; PDF drops also open new tabs.
Each tab retains its latest saved PDF and its page, zoom and renderer settings.
Inactive PDF renderers are released and rebuilt when their tab is activated.
Unsaved inspector drafts are not persisted across tab switches.

**Split PDF** opens one new tab per page. **Extract pages** accepts ordered
selections such as `1, 3, 5-7` or `3-1` and opens one new tab containing the selected
pages. Duplicate or out-of-range pages are rejected. Both operations use the
current saved PDF and leave the original tab open. Unsupported assembly features
produce an error with no partial tabs.

Documents live in browser memory: download results before closing their tabs or
reloading the page. The playground permits 128 tabs, up to 100 split/extracted
pages per operation, 20 MB per input/output document and 64 MB total output per
assembly request. Tab switching is disabled while an edit, render, dialog or
object-creation operation is active.

### Tagged PDF page assembly

Split, ExtractPages, SplitPages and Merge preserve supported accessibility
structure automatically. No tag-removal option is needed or exposed.

The copier prunes structure content belonging to excluded pages, retains
semantic containers and attributes (including Alt and ActualText), and keeps
page MCIDs unchanged. It rebuilds the ParentTree and renumbers StructParents/
StructParent keys for pages, tagged Form XObjects and annotations. Parent links,
page links, custom roles/classes, structure IDs and IDTree entries are remapped.
Custom role/class names and structure IDs receive source prefixes when copied
to avoid collisions. Table-header ID references are updated accordingly.

Original logical structure order is retained within each input, even when the
requested physical page order differs. Merged document structures follow input
order. MarkInfo and XMP metadata are retained; mixing untagged and tagged pages
marks the result as Suspects rather than claiming that new content was tagged.

Malformed/cyclic structure, inconsistent parent-tree back references, unsupported
root extensions, and table-header dependencies on excluded structure elements
are rejected explicitly. This is structural preservation, not automatic
accessibility repair or a guarantee of PDF/UA conformance. Review accessibility
after significant page rearrangements.

Regression coverage includes page pruning/reordering, custom roles/classes,
ActualText/Alt, annotation OBJR references, Form XObject MCR references, ID trees,
merged parent-key collisions, source immutability, and independent PDFsharp
reads. Set PDFOBJECTKIT_TAGGED_TEST_PDF to a local PDF path when running the
backend console tests to additionally verify splitting that document.

## Comments and text markup

`PdfAnnotationEditor` creates standard PDF comments (`Text`), highlights,
underlines and strikethroughs (`StrikeOut`). Annotations preserve the underlying
page content and include saved appearances for PDF.js and the experimental
native renderer. Comment text and author names support Unicode.

```csharp
PdfAnnotationEditor.Add(document, 1, PdfAnnotationKind.Comment,
    [new PdfAnnotationRegion(40, 650, 24, 24)], "Please review", "Reviewer");
PdfAnnotationEditor.Add(document, 1, PdfAnnotationKind.Highlight,
    [new PdfAnnotationRegion(40, 600, 180, 16),
     new PdfAnnotationRegion(40, 580, 120, 16)], "Check these lines");
var annotations = PdfAnnotationExtractor.Extract(document);
PdfAnnotationEditor.UpdateContents(document, 1, annotations[0].AnnotationNumber, "Reviewed");
PdfAnnotationEditor.Delete(document, 1, annotations[0].AnnotationNumber);
```

Coordinates use unrotated PDF page space in points, with the origin at the
bottom left. Supply one rectangle per line for multiline markup. Annotation
numbers are one-based positions in the page annotation array; re-extract after
changes. In the playground, use the Comment tool to place a note. Highlight, Underline and Strikethrough
apply only to selected text: select one or multiple text lines and use
**Annotate selected text** in the Inspector. Select an annotation to edit its
comment or delete it; Ctrl/Cmd selection and Delete also work for annotations.
Download PDF includes the saved annotations.

This first version uses fixed yellow highlighting and orange lines, and
axis-aligned regions. Select text mode supports character-level selection. Imported annotations
are listed for these four subtypes; the native renderer needs their saved normal
appearance. Signed documents and locked annotations are protected. Deleting
annotations with structure-tree references or replies is rejected rather than
leaving dangling references. Creating annotations does not automatically add
accessibility tags, and deletion is not secure redaction of underlying content.

Clicking text covered by highlight, underline or strikethrough selects the text
so it can still be moved. Alt+click to select overlapping annotations, or select the markup in the object list to edit or delete it.
Comments remain directly selectable on the canvas. Moving text does not move its annotations.

### Playground interaction modes

Use the toolbar mode icons (with tooltips and active-state highlighting):

- **Select objects**: select, Ctrl/Cmd-toggle, rectangle-select, and move objects.
- **Select text**: drag over characters or words, then choose Highlight, Underline
  or Strikethrough in the Inspector. Each selected text fragment receives its own
  region, including passages spanning lines. Escape clears the selection.
- **Pan**: drag to scroll the workspace without selecting or modifying content.

Partial selection uses PDF.js's transparent text layer with either renderer.
Page rotation, crop and zoom are mapped back to PDF coordinates. Scanned pages
need OCR before text selection is available. Regions are axis-aligned in PDF
space; arbitrary skewed/rotated text within a page may have looser bounds.
Changing page, zoom, document or mode clears the passage selection to avoid stale
coordinates. Creating an object or choosing one in the object list returns to
Select objects mode.

### Merge open playground tabs

Check the selection box on two or more PDF tabs, or Ctrl/Cmd-click tabs, then
choose **Merge selected**. The first Ctrl/Cmd-click also includes the active PDF
when starting a selection. Files are merged in their visible left-to-right tab
order, using each tab's latest saved edits. The result opens in a new `merged.pdf`
tab; all source tabs remain unchanged. Finish or cancel an in-progress draft
before merging. Failed merges retain the source tabs and selection.

The merge API accepts 2?32 multipart `files`, at most 20 MB each and 64 MB total.
The output is limited to 100 pages and 20 MB so it can be reopened in the
playground. Form field names are prefixed per source (`Document1_`, etc.), and
colliding attachment and destination names are renamed using the existing
assembly policies. Unsupported document features are reported without creating
a partial result.

Drag a tab onto the left or right edge of another tab to reorder it. The blue
insertion indicator shows the destination. With a tab focused, Alt+Left/Right
moves it one position without changing the active document. Reordering retains
edits, view state and merge selections; Merge selected uses the updated order.

Use **Delete page** beside the page controls to remove the current page after confirmation. The current tab is updated using the saved PDF, and the nearest remaining page is selected. The last page cannot be deleted. Download the result to keep it. Library clients can call `PdfPageEditor.DeletePage(document, pageNumber, options)`; it returns an independent document and leaves the source unchanged, using the same preservation rules and limits as page extraction.
