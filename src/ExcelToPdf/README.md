# Excel to PDF

The same CLI / API / proxy / React 19 playground structure as `HtmlToPdf`,
using LibreOffice Calc for `.xlsx` and `.xls` files.

| Project | Purpose |
| --- | --- |
| `ExcelToPdf.LibreOffice.Cli` | Convert a local file from the command line |
| `ExcelToPdf.LibreOffice.Api` | Multipart file conversion API |
| `ExcelToPdf.Proxy` | YARP gateway at `/libreoffice/*` |
| `ExcelToPdf.Playground` | Drag/drop or browse, then view or download the PDF |

LibreOffice execution lives in the API project and is linked into its CLI. Excel and Word have independent converter implementations. Each conversion has an
isolated temporary directory and LibreOffice profile, macros disabled, a
90-second timeout, process-tree termination on cancellation, and cleanup.

## Docker Compose

From this directory:

```powershell
docker compose up --build -d
```

| Service | URL |
| --- | --- |
| Playground | http://localhost:8094 |
| Proxy | http://localhost:8090 |
| LibreOffice API | http://localhost:8091 |

The .NET Dockerfiles use this solution directory as their build context.
Their `Dockerfile.dockerignore` excludes local `bin/` and `obj/` output.
Containers include LibreOffice and basic fonts, and run as a non-root user.

## Local development

Requires .NET 10 SDK, Node.js 22.12+, npm, and LibreOffice.
Set `LIBREOFFICE_BIN` if LibreOffice is not at the standard Windows location
or available as `soffice` on PATH on Linux. Start these in separate terminals:

```powershell
dotnet build ./ExcelToPdf.slnx
dotnet run --project ./ExcelToPdf.LibreOffice.Api
dotnet run --project ./ExcelToPdf.Proxy
```

In `ExcelToPdf.Playground`, run `npm ci` and `npm run dev`, then open
http://localhost:5175. Vite forwards `/api/*` to the proxy at
http://localhost:62723, removing `/api`.
Copy `.env.sample` to `.env` to configure `EXCEL_TO_PDF_PROXY_URL`; use
`http://localhost:8090` for a Compose backend. Restart Vite after edits.
Docker uses Nginx forwarding to the proxy service, not the Vite environment variable.

## API

`POST /libreoffice/convert` on the proxy accepts `multipart/form-data` field
`file`, with a non-empty `.xlsx` or `.xls` upload up to 10 MB.
The API itself exposes `POST /convert` and `GET /health`; the proxy also has
`GET /health`. Success returns `application/pdf` with a download filename.
Invalid uploads return 400, unconvertible documents 422, missing LibreOffice
503, and conversion timeouts 504. Password-protected documents are unsupported.

```powershell
curl.exe -f -F "file=@sample.xlsx" http://localhost:8090/libreoffice/convert -o sample.pdf
```

## CLI

```powershell
dotnet run --project ./ExcelToPdf.LibreOffice.Cli -- input.xlsx output.pdf
```

The output path is optional (defaults to the input filename with `.pdf`).
An existing output PDF is overwritten. The CLI returns a nonzero exit code on failure.
The old nested `ExcelToPdf/LibreOfficeConverter` sample is replaced by these projects.

## Verification

Run `dotnet build ./ExcelToPdf.slnx`. In the playground, run `npm run build` and
`npm run test:e2e` (installed Google Chrome required). UI tests mock conversion
responses. For real conversion, start the Compose services and use the curl
example above with a valid document. Fonts and LibreOffice layout may differ
from Microsoft Office; set workbook print areas and scaling before upload.

To run the live browser/API check against Docker Compose:

```powershell
$env:EXCEL_TO_PDF_PROXY_URL = "http://localhost:8090"
$env:EXCEL_TO_PDF_LIVE_TEST = "1"
npm run test:e2e
```

Run these commands from the playground directory.
