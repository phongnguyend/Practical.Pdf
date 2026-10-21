# PdfObjectKit

C# / .NET 10 library and CLI for reading, editing, and writing PDF object graphs. They load a PDF's current indirect objects into memory, serialize them to JSON, and rebuild a PDF from that JSON. Reconstruction operates at the PDF object level, without re-rendering pages or embedding the entire original file. Exports also include derived text and image indexes by default.

## Run

The object graph is read by the handwritten C# parser in `PdfObjectKit/Parsers`, with **no NuGet dependencies**. It handles PDF values, byte strings, indirect references, length-delimited streams, classic cross-reference tables, cross-reference streams, compressed object streams, hybrid references, and incremental revisions. Stream bytes remain Base64 in the model; numeric tokens retain their original precision.

To use the parser directly, reference `PdfObjectKit/PdfObjectKit.csproj`:

```csharp
using PdfObjectKit.Parsers;
var document = PdfNativeParser.Parse(File.ReadAllBytes("input.pdf"));
```

The parser, main library, and CLI have **no NuGet dependencies**. Content interpretation, text/image indexes, run editing, and TrueType embedding are implemented in C#. PDFsharp is used only by the test project as an independent reader. `PdfReader.Read(bytes, includeIndexes: false)` skips indexing. The equivalent CLI option is:

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

## Use from C#

Reference `PdfObjectKit/PdfObjectKit.csproj`:

| API | Responsibility |
| --- | --- |
| `PdfReader.Read(bytes, includeIndexes: true)` | Parse a PDF and optionally build text/image indexes. |
| `PdfWriter.Write(document)` | Rebuild a PDF from its objects; indexes are informational. |
| `PdfTextEditor.UpdateText(document, pageNumber, run, text, textOptions: null)` | Replace one text operand and refresh indexes. |
| `PdfConverter.ToJson(document)` | Serialize an object graph to JSON. |
| `PdfConverter.FromJson(json)` | Deserialize and validate an object graph. |

These replace the previous combined object converter. Structural validation is shared internally by the reader, writer, converter, and editor APIs. The object-graph JSON schema is unchanged. The CLI supports `to-json` and `to-pdf` only; the legacy text-only conversion and original-file restoration commands have been removed.

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

## JSON representation

Exports include a derived `textIndex` with one entry per page (including blank pages):

```json
"textIndex": [
  {
    "pageNumber": 1,
    "text": "Hello world",
    "contentStreams": [{ "objectNumber": 10, "generation": 0 }],
    "formStreams": []
  }
]
```

Search `textIndex` for readable text, then use its references to find streams in `objects`. `contentStreams` lists page content streams; `formStreams` lists Form XObject resource candidates, including nested forms and inherited resources. These are page-level navigation references, **not exact text-to-stream mappings**: a content stream may contain only graphics, and a listed form may not be invoked. Reading order and characters depend on font mappings. No OCR is performed; annotation appearances are not separately indexed. A `warning` identifies incomplete indexing without preventing structural export.

### Image index

Exports also include `imageIndex`, with one entry per page, including pages without images:

```json
"imageIndex": [
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

The method returns the new image object's reference and updates the supplied document only after success. It refreshes both indexes, isolates the existing graphics state, and creates page-local resources without changing shared resource dictionaries or original content streams. Reacquire object/page references from the document after insertion because its object graph is replaced.

Supported input is 8-bit grayscale or three-component DCT JPEG (baseline, extended sequential, or progressive), and non-interlaced PNG (1-bit grayscale, or 8-bit grayscale, RGB, palette, grayscale-alpha, or RGBA). PNG transparency becomes a PDF soft mask; PDF versions older than 1.4 are upgraded when needed. PNG pixels are decoded and recompressed; JPEG bytes are embedded intact after header validation, without decoding the JPEG entropy data. Images are limited to 16 million pixels. CMYK JPEG, other PNG sample depths, interlaced/animated PNG, and other image formats are rejected. Embedded color profiles and image metadata are not imported; color management is not implemented.

The target page's content must be readable by the native content parser, with balanced `q`/`Q` and `BT`/`ET` operations. Unsupported filters or filtered inline images can prevent insertion. The helper adds content streams around the original sequence. Consequently, the existing text editor's one-content-stream restriction applies afterward: apply text edits before insertion. The refreshed page index reports this through `runWarning`. Other pages retain their editable runs where supported. Image insertion is currently a C# API, not a CLI command.

### Editing text in C#

`textIndex` is inspection data. Changing its page text or run text in JSON does **not** change the rebuilt PDF. `PdfWriter.Write` writes the object graph only. Use `PdfTextEditor` to update a specific text operand:

```csharp
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using PdfObjectKit.Editors;

var document = PdfReader.Read(File.ReadAllBytes("input.pdf"));
var run = document.TextIndex[0].Runs!.First(r => r.Text == "Application Form");
PdfTextEditor.UpdateText(document, 1, run, "Application XX Form");
File.WriteAllBytes("updated.pdf", PdfWriter.Write(document));
```

Page numbers are one-based. The selected run identifies a stream object, operator, and string operand, so repeated text elsewhere stays unchanged. The run overload validates the stream checksum and operand identity. Reacquire runs from the refreshed index after each successful edit. Passing an empty replacement deletes that operand's text.

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

The entire changed operand uses the fallback font when needed. The editor verifies the resulting extracted text before updating the supplied document; failure leaves it unchanged. Both indexes are refreshed on success. JSON serialization preserves the resulting object changes, but editing index fields itself has no effect. The CLI's former `--font` option is removed.

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
