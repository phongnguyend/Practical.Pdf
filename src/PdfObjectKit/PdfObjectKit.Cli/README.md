# PdfObjectKit CLI

Offline command-line equivalents of the Playground's document operations. No API server is needed. The CLI and Playground API share edit handling, inspection, and Markdown options through `PdfObjectKit.Application`.

From the repository root:

```sh
dotnet run --project src/PdfObjectKit/PdfObjectKit.Cli -- --help
dotnet run --project src/PdfObjectKit/PdfObjectKit.Cli -- inspect input.pdf inspect.json
```

The examples below use `pdfobjectkit` as shorthand for `dotnet /path/to/PdfObjectKit.Cli.dll`. Publish with:

```sh
dotnet publish src/PdfObjectKit/PdfObjectKit.Cli -c Release -o artifacts/pdf-cli
```

## Commands

Every command takes an input and output path. Add `--overwrite` to replace an existing output. Inputs, option files, and used assets cannot also be outputs. Multi-file outputs are fully prepared and staged before publishing, but publishing all files is not a single filesystem transaction. Use a fresh output directory for split and comparison results to avoid stale files from an earlier run.

| Operation | Command |
| --- | --- |
| PDF object graph and derived indexes | `to-json input.pdf graph.json [--objects-only]` |
| Rebuild object graph | `to-pdf graph.json output.pdf` |
| Markdown, including table/column hints | `to-markdown input.pdf output.md [--options markdown.json]` |
| Inspect selectable objects and source runs | `inspect input.pdf inspection.json` |
| Inspect specific data | `metadata`, `forms`, `annotations`, or `watermarks`, followed by input PDF and output JSON |
| Text search with match geometry | `search input.pdf matches.json --query "search text" [--options search.json]` |
| Edit objects and document properties | `edit input.pdf output.pdf --options edit.json` |
| One PDF per page | `split input.pdf output-directory` |
| Extract pages in supplied order | `extract input.pdf output.pdf --pages 3,1-2` |
| Reorder every page | `reorder-pages input.pdf output.pdf --pages 3,1,2` |
| Delete / duplicate page | `delete-page` / `duplicate-page input.pdf output.pdf --page 2` |
| Insert a blank page after a page | `insert-blank-page input.pdf output.pdf --page 2` |
| Append a blank page | `insert-blank-page input.pdf output.pdf --page last` |
| Rotate a page | `rotate-page input.pdf output.pdf --page 1 --degrees 90` |
| Merge PDFs | `merge inputs.json output.pdf [--options assembly.json]` |
| Flatten forms and annotations | `flatten input.pdf output.pdf [--options flatten.json]` |
| Lossless compression | `compress input.pdf output.pdf [--options compression.json]` |
| Decrypt / remove restrictions with authorized access | `unlock input.pdf output.pdf --password-env PDF_PASSWORD` |
| AES-256 password protection and permissions | `protect input.pdf output.pdf --options security.json` |
| Preview content removed by redaction | `redaction-preview input.pdf preview.json --options regions.json` |
| Apply content-preserving redaction | `redact input.pdf output.pdf --options regions.json` |
| Experimental native renderer display list | `render-data input.pdf page.json --page 1` |
| Decoded source image data | `image-data input.pdf image.json --page 1 --image 1` |

Page and image numbers start at **1**. `widgetIndex` starts at **0**. Coordinates and distances use unrotated PDF user space, generally points with a bottom-left origin. OCR lines instead use normalized image coordinates with a top-left origin.

Page assembly commands accept `--options` using `PdfPageAssemblyOptions`. Merge inputs are a JSON array of paths relative to the manifest, e.g. `["first.pdf", "subfolder/second.pdf"]`. By default merge namespaces form fields and renames conflicting attachment/destination names, matching the Playground. Supplying assembly options replaces these defaults with the values in that object.

The library's unsupported-feature checks remain in effect. Full rewrites do not preserve existing digital signatures. Unlock encrypted documents before normal operations; use `protect` on the result if needed. Restricted PDFs require owner authorization, as in the Playground.

## Editing

An edit JSON file uses the Playground's `EditRequest` model. `value` defaults to an empty string in the CLI when omitted. Enum names are strings. Asset paths are separate command options rather than embedded file bytes.

```json
{
  "kind": "add-text",
  "pageNumber": 1,
  "value": "Hello",
  "x": 40,
  "y": 700,
  "fontSize": 18
}
```

```sh
pdfobjectkit edit input.pdf output.pdf --options edit.json
```

| Edit kind | Additional properties / assets |
| --- | --- |
| `add-text` | `pageNumber`, `value`, `x`, `y`, `fontSize` |
| `text` | `pageNumber`, `run`, `value` |
| `move-text` | `pageNumber`, `runs`, `deltaX`, `deltaY` |
| `delete-text-line` | `pageNumber`, `runs` |
| `add-image` | `pageNumber`, `x`, `y`, `width`, `height`; `--image-file image.png` |
| `replace-image` | `pageNumber`, `imageNumber`, `imageFit` (`Fit`, `Fill`, or `Stretch`); `--image-file image.png` |
| `move-image` | `pageNumber`, `imageNumber`, `deltaX`, `deltaY` |
| `delete-image` | `pageNumber`, `imageNumber` |
| `add-field` | `pageNumber`, `fieldName`, `fieldType`, `value`, rectangle and field-specific options |
| `field` | `fieldName`, `value` or `values`, optional `textAlignment` |
| `rename-field` | `fieldName`, `value` containing the new name |
| `move-field` | `fieldName`, `widgetIndex`, `deltaX`, `deltaY` |
| `delete-field` | `fieldName` |
| `rich-text` | `fieldName`, `value` containing supported XHTML, optional `textAlignment` |
| `button-caption` | `fieldName`, `value` |
| `button-action` | `fieldName`, `action` (null removes it) |
| `sign-field` | `fieldName`, `value` containing signing reason; `--certificate-file identity.pfx --certificate-password-env CERT_PASSWORD` |
| `add-comment`, `add-highlight`, `add-underline`, `add-strikeout` | `pageNumber`, `value`, `regions` array of rectangles or `x`, `y`, `width`, `height` |
| `annotation` | `pageNumber`, `annotationNumber`, `value` |
| `delete-annotation` | `pageNumber`, `annotationNumber` |
| `delete-objects` | `pageNumber`, `deletions` array of text-line/image/field/annotation delete requests |
| `metadata` | `metadata: { "fields": { "Title": "New title", "Author": "Author" } }` |
| `add-watermark` | `watermark` options |
| `update-watermark` | `watermarkId`, `watermark` options |
| `remove-watermark` | `watermarkId` from `watermarks` output |
| `apply-image-ocr` | `pageNumber`, `imageNumber`, `ocrLines`, `replaceOcrImage`; `--font-file font.ttf` |
| Page operations | `rotate-page`, `delete-page`, `duplicate-page`, `insert-blank-page`, `reorder-pages` also accept the Playground's edit model |

Supported field types: `Text`, `CheckBox`, `Choice`, `RadioButton`, `PushButton`, `Signature`. Text options include `multiline`, `password`, `comb`, `fileSelect`, `richText`, `maxLength`, and `textAlignment` (`Left`, `Center`, `Right`). Choice options include `choices`, `listBox`, `multiSelect`, and `values`. See [EditRequest.cs](../PdfObjectKit.Application/EditRequest.cs) for every property and default.

Use `inspect` on the **current** saved PDF to obtain `objects[].details.runs`. Copy the complete run object, including its checksum, into `run`, or its complete list into `runs`. Run identities become stale after editing. Search results provide geometry for partial-text annotations; pass only the chosen text rectangles. A CLI annotation command cannot infer which text a person selected in a GUI.

Metadata fields omitted from `metadata.fields` stay unchanged; null removes a field. Dates use ISO 8601 with timezone. Modification date updates automatically unless explicitly set or removed. For example:

```json
{"kind":"metadata","metadata":{"fields":{"Title":"Quarterly report","CreationDate":"2026-10-31T09:00:00+07:00"}}}
```

Watermark example:

```json
{"kind":"add-watermark","watermark":{"text":"DRAFT","opacity":0.2,"rotation":45,"fontSize":48,"pageNumbers":[1,2]}}
```

## Conversion and security options

`--options` reads strict JSON: unknown members, unknown flags, and duplicate flags are errors. Supported schemas are the same library option models used by the Playground.

Markdown options, including a manually hinted three-column table:

```json
{
  "includeImages": true,
  "joinHyphenatedWords": false,
  "bodyTextHeight": null,
  "heuristics": {},
  "tableHints": [
    { "pageNumber": 1, "x": 30, "y": 400, "width": 500, "height": 120,
      "firstRowIsHeader": false, "columnBoundaries": [350, 450] }
  ]
}
```

Search: `{"matchCase":false,"wholeWord":true,"maxResults":500}`.

Flatten: `{"formFields":true,"annotations":true}`.

Compression: `{"removeUnusedObjects":true,"compressStreams":true,"deduplicateResources":true}`.

Security: `{"userPassword":"open-password","ownerPassword":"different-owner-password","allowCopying":false,"allowModifying":false}`. Other permissions default to allowed. Keep this options file private. Input PDF and certificate passwords can be supplied through environment variable names rather than command-line password values.

Redaction regions use the same PDF coordinates for content and raster modes:

```json
[{ "pageNumber": 1, "x": 40, "y": 600, "width": 140, "height": 24 }]
```

Review the content redaction preview before applying. Content-preserving mode retains supported unrelated text and graphics and rejects unsupported content rather than silently falling back to rasterization. Raster mode produces a fresh image-only document and removes text search, forms, annotations, metadata, and other original structures.

## Optional rendering, comparison, OCR and QR commands

These commands need **Node.js 22.13+** and the optional helper's dependencies. Core .NET commands have no npm or NuGet dependency. The Docker CLI image includes the helper and Node.

For a source checkout:

```sh
npm ci --prefix src/PdfObjectKit/PdfObjectKit.Cli/visual
```

For a published installation:

```sh
npm ci --prefix artifacts/pdf-cli/visual
```

The CLI discovers the adjacent helper or the installed source helper during `dotnet run`. Override it using `--helper /path/to/visual/visual.mjs`.

```sh
pdfobjectkit render input.pdf page.png --page 1 --dpi 144
pdfobjectkit export-image input.pdf image.png --page 1 --image 1
pdfobjectkit rasterize input.pdf image-only.pdf --dpi 144
pdfobjectkit redact-raster input.pdf redacted.pdf --options regions.json --dpi 144
pdfobjectkit compare a.pdf comparison-directory --other b.pdf
pdfobjectkit qr text.txt qr.png
pdfobjectkit ocr image.png lines.json --language eng
```

Rendering uses PDF.js with local fonts, CMaps, and WASM. Exported images are PNGs at source pixel size, with supported transparency preserved; page clipping and rotation are excluded. Comparison writes `index.html`, A/B/difference PNGs, and `comparison.json`. It pairs pages by number and uses the Playground's green/addition, red/removal, amber/change convention and pixel threshold. Reverse the two PDF inputs to swap A/B. This is visual comparison, not semantic or metadata comparison.

Rasterization supports up to 100 pages, 16 million pixels per page, and 64 MB of RGB pixels combined. Lower DPI or extract fewer pages when limits are exceeded. These limits also apply to raster redaction; marks outside the visible page are rejected.

OCR runs locally with bundled English (`eng`) or Vietnamese (`vie`) trained data. Additional three-letter languages require `--data-dir` pointing to a directory containing `<language>.traineddata.gz`; there is no automatic language download. Output is an editable JSON array of normalized `PdfOcrTextLine` records, ready for `--ocr-lines`:

```json
{"kind":"apply-image-ocr","pageNumber":1,"imageNumber":1,"replaceOcrImage":false}
```

```sh
pdfobjectkit edit source.pdf searchable.pdf --options apply-ocr.json --ocr-lines lines.json --font-file NotoSans-Regular.ttf
```

Set `replaceOcrImage` to true to replace that image occurrence with visible text. Otherwise the image stays and the added text is invisible and searchable. Extract the selected source image before OCR so its normalized coordinates match the target image. The Playground's `public/fonts/NotoSans-Regular.ttf` is available under its accompanying SIL license.

The native renderer is exposed as `render-data` for display-list inspection; PNG rendering uses PDF.js. GUI-only interactionsâ€”tabs, drag handles, drawing canvas, theme, undo/redoâ€”are represented by explicit input/output files and edit coordinates, rather than an interactive terminal UI. Keep earlier output files to undo an operation.

## Verification

```sh
dotnet run --project src/PdfObjectKit/PdfObjectKit.Cli.Tests
dotnet run --project src/PdfObjectKit/PdfObjectKit.Cli.Tests -- --visual src/PdfObjectKit/PdfObjectKit.Cli/visual/visual.mjs
```

CI runs both core and visual CLI checks before the Playground E2E suite.
