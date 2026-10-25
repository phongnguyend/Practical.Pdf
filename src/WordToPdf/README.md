# Word to PDF

The same CLI / API / proxy / React 19 playground structure as `HtmlToPdf`,
using LibreOffice Writer for `.docx` and `.doc` files.

| Project | Purpose |
| --- | --- |
| `WordToPdf.LibreOffice.Cli` | Convert a local file from the command line |
| `WordToPdf.LibreOffice.Api` | Multipart file conversion API |
| `WordToPdf.Proxy` | YARP gateway at `/libreoffice/*` |
| `WordToPdf.Playground` | Drag/drop or browse, then view or download the PDF |

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
| Playground | http://localhost:8104 |
| Proxy | http://localhost:8100 |
| LibreOffice API | http://localhost:8101 |

The .NET Dockerfiles use this solution directory as their build context.
Their `Dockerfile.dockerignore` excludes local `bin/` and `obj/` output.
Containers include LibreOffice and basic fonts, and run as a non-root user.

## Local development

Requires .NET 10 SDK, Node.js 22.12+, npm, and LibreOffice.
Set `LIBREOFFICE_BIN` if LibreOffice is not at the standard Windows location
or available as `soffice` on PATH on Linux. Start these in separate terminals:

```powershell
dotnet build ./WordToPdf.slnx
dotnet run --project ./WordToPdf.LibreOffice.Api
dotnet run --project ./WordToPdf.Proxy
```

In `WordToPdf.Playground`, run `npm ci` and `npm run dev`, then open
http://localhost:5176. Vite forwards `/api/*` to the proxy at
http://localhost:62823, removing `/api`.
Copy `.env.sample` to `.env` to configure `WORD_TO_PDF_PROXY_URL`; use
`http://localhost:8100` for a Compose backend. Restart Vite after edits.
Docker uses Nginx forwarding to the proxy service, not the Vite environment variable.

## API

`POST /libreoffice/convert` on the proxy accepts `multipart/form-data` field
`file`, with a non-empty `.docx` or `.doc` upload up to 10 MB.
The API itself exposes `POST /convert` and `GET /health`; the proxy also has
`GET /health`. Success returns `application/pdf` with a download filename.
Invalid uploads return 400, unconvertible documents 422, missing LibreOffice
503, and conversion timeouts 504. Password-protected documents are unsupported.

```powershell
curl.exe -f -F "file=@sample.docx" http://localhost:8100/libreoffice/convert -o sample.pdf
```

## CLI

```powershell
dotnet run --project ./WordToPdf.LibreOffice.Cli -- input.docx output.pdf
```

The output path is optional (defaults to the input filename with `.pdf`).
An existing output PDF is overwritten. The CLI returns a nonzero exit code on failure.
The old nested `WordToPdf/LibreOfficeConverter` sample is replaced by these projects.

## Verification

Run `dotnet build ./WordToPdf.slnx`. In the playground, run `npm run build` and
`npm run test:e2e` (installed Google Chrome required). UI tests mock conversion
responses. For real conversion, start the Compose services and use the curl
example above with a valid document. Fonts and LibreOffice layout may differ
from Microsoft Office; install matching fonts for closest layout.

To run the live browser/API check against Docker Compose:

```powershell
$env:WORD_TO_PDF_PROXY_URL = "http://localhost:8100"
$env:WORD_TO_PDF_LIVE_TEST = "1"
npm run test:e2e
```

Run these commands from the playground directory.
