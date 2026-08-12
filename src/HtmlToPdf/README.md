# HTML-to-PDF converters

This solution demonstrates three ways to convert HTML into PDF with .NET 10:

| Implementation | Console project | HTTP API project | Browser |
| --- | --- | --- | --- |
| Chrome CLI | `ChromeCliConverter` | `ChromeCliConverter.Api` | Google Chrome Stable |
| Playwright | `PlaywrightConverter` | `PlaywrightConverter.Api` | Playwright Chromium 1.61.0 |
| PuppeteerSharp | `PuppeteerSharpConverter` | `PuppeteerSharpConverter.Api` | Google Chrome Stable |

## Prerequisites

- .NET 10 SDK for local builds
- Docker Desktop or Docker Engine for container builds
- Internet access while building images and when uploaded HTML loads remote
  assets such as images, fonts, or stylesheets

## Build the solution

From the `src/HtmlToPdf` directory:

```powershell
dotnet build .\HtmlToPdf.slnx
```

## Converter APIs

All three API projects expose the same contract:

- `POST /convert` accepts `multipart/form-data` with an HTML file in the `file`
  field.
- The upload must have an `.html` or `.htm` extension.
- The maximum HTML file size is 10 MB.
- A successful request returns an `application/pdf` download.
- `GET /health` returns the service health status.

### Build the API images

Run these commands from `src/HtmlToPdf`:

```powershell
docker build -t chrome-cli-api .\ChromeCliConverter.Api
docker build -t playwright-api .\PlaywrightConverter.Api
docker build -t puppeteer-sharp-api .\PuppeteerSharpConverter.Api
```

On Linux or macOS, replace `\` with `/`:

```bash
docker build -t chrome-cli-api ./ChromeCliConverter.Api
docker build -t playwright-api ./PlaywrightConverter.Api
docker build -t puppeteer-sharp-api ./PuppeteerSharpConverter.Api
```

### Run an API image

Each container listens on port `8080`. Run one implementation at a time with
the same host port:

```powershell
docker run --rm --init -p 8080:8080 chrome-cli-api
```

```powershell
docker run --rm --init --ipc=host -p 8080:8080 playwright-api
```

```powershell
docker run --rm --init -p 8080:8080 puppeteer-sharp-api
```

Playwright recommends `--ipc=host` for Chromium to avoid shared-memory crashes.

To run all implementations simultaneously, map each one to a different host
port:

```powershell
docker run --rm --init -d -p 8081:8080 --name chrome-cli-api chrome-cli-api
docker run --rm --init --ipc=host -d -p 8082:8080 --name playwright-api playwright-api
docker run --rm --init -d -p 8083:8080 --name puppeteer-api puppeteer-sharp-api
```

### Convert an HTML file

With an API mapped to `localhost:8080`, use `curl.exe` from PowerShell:

```powershell
curl.exe -X POST `
  -F "file=@sample.html;type=text/html" `
  -o result.pdf `
  http://localhost:8080/convert
```

Linux or macOS:

```bash
curl -X POST \
  -F "file=@sample.html;type=text/html" \
  -o result.pdf \
  http://localhost:8080/convert
```

For the simultaneous-container example, use ports `8081`, `8082`, and `8083`
to compare the generated PDFs.

### Check API health

```powershell
curl.exe http://localhost:8080/health
```

Expected response:

```json
{"status":"healthy"}
```

## Run an API locally

Google Chrome must be installed to run `ChromeCliConverter.Api` or
`PuppeteerSharpConverter.Api` locally:

```powershell
dotnet run --project .\ChromeCliConverter.Api
dotnet run --project .\PuppeteerSharpConverter.Api
```

For `PlaywrightConverter.Api`, build the project and install its matching
Chromium binary first:

```powershell
dotnet build .\PlaywrightConverter.Api\PlaywrightConverter.Api.csproj
pwsh .\PlaywrightConverter.Api\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet run --project .\PlaywrightConverter.Api
```

The local development port is printed by ASP.NET Core when the project starts.
Use that URL in place of `http://localhost:8080` in the examples above.

## Console applications

The solution also contains one-shot console converters. Their individual
documentation includes image build commands, output volume mounting, and local
browser setup:

- [`ChromeCliConverter/README.md`](ChromeCliConverter/README.md)
- [`PlaywrightConverter/README.md`](PlaywrightConverter/README.md)
- [`PuppeteerSharpConverter/README.md`](PuppeteerSharpConverter/README.md)

## Security notes

- The browser containers run as `root` and launch Chrome or Chromium with
  `--no-sandbox`. Only convert trusted HTML in this configuration.
- Uploaded HTML can cause the browser to request remote URLs. Do not expose
  these sample APIs publicly without authentication, request throttling,
  network restrictions, and stronger browser isolation.
- Google Chrome package installation in the Chrome CLI and PuppeteerSharp
  images targets `amd64`/x86-64 hosts.

## Troubleshooting

- If Docker cannot connect to its daemon, start Docker Desktop or the Docker
  service.
- If Playwright reports that its executable does not exist, run the generated
  `playwright.ps1 install chromium` command shown above.
- If conversion fails while loading remote assets, confirm that the container
  has internet and DNS access.
- Keep the Playwright Docker image tag synchronized with the
  `Microsoft.Playwright` package version.
