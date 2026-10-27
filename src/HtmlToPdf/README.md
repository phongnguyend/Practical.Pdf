# HTML-to-PDF converters

This solution demonstrates three ways to convert HTML into PDF with .NET 10:

| Implementation | Console project | HTTP API project | Browser |
| --- | --- | --- | --- |
| Chrome CLI | `HtmlToPdf.Chrome.Cli` | `HtmlToPdf.Chrome.Api` | Google Chrome Stable |
| Playwright | `HtmlToPdf.Playwright.Cli` | `HtmlToPdf.Playwright.Api` | Playwright Chromium 1.61.0 |
| PuppeteerSharp | `HtmlToPdf.PuppeteerSharp.Cli` | `HtmlToPdf.PuppeteerSharp.Api` | Google Chrome Stable |

`HtmlToPdf.Proxy` provides a single YARP reverse-proxy endpoint for all three
HTTP APIs.

## React playground

[`HtmlToPdf.Playground`](HtmlToPdf.Playground/README.md) provides a React 19 UI
for dragging or uploading HTML, selecting a rendering engine, and downloading
a PDF through `HtmlToPdf.Proxy`. With Docker Compose, open http://localhost:8084.
For local development, run `npm ci` and `npm run dev` in `HtmlToPdf.Playground`,
then open http://localhost:5174 with the proxy and selected converter running.

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

### Convert a webpage URL

All three APIs also expose `POST /convert-url` with JSON `{ "url": "https://example.com" }`.
Through the proxy, use `/chrome/convert-url`, `/playwright/convert-url`, or
`/puppeteer/convert-url`. Only absolute HTTP/HTTPS URLs without embedded
credentials are accepted. The browser navigates to the address directly, so
relative CSS, images, and scripts resolve against the webpage URL.

The playground's **Website URL** option supports the same engine selection,
individual results, PDF viewing, and downloads as file uploads. Pages open in
a fresh server-side browser without the user's signed-in cookies. The address
must be reachable from the converter (inside Docker, `localhost` means that container).
URL conversion has a 90-second overall limit. Playwright and PuppeteerSharp
wait for page load and reject HTTP error responses; Chrome CLI may print a
browser or HTTP error page because it does not expose navigation status.

```powershell
Invoke-WebRequest -Uri http://localhost:8080/playwright/convert-url -Method Post -ContentType application/json -Body '{"url":"https://example.com"}' -OutFile webpage.pdf
```

The CLIs also accept a URL as their second argument, preserving the first
argument as the output path:

```powershell
dotnet run --project ./HtmlToPdf.Playwright.Cli -- webpage.pdf https://example.com
```

The same syntax works for `HtmlToPdf.Chrome.Cli` and `HtmlToPdf.PuppeteerSharp.Cli`.

All three API projects expose the same contract:

- `POST /convert-file` accepts `multipart/form-data` with an HTML file in the `file`
  field.
- The upload must have an `.html` or `.htm` extension.
- The maximum HTML file size is 10 MB.
- A successful request returns an `application/pdf` download.
- `GET /health` returns the service health status.

### Run all APIs with Docker Compose

To use released Docker Hub images, follow the [published-image setup](../../README.md#run-published-docker-images)
and run `docker compose -f compose.published.yaml up -d` after configuring
`DOCKERHUB_NAMESPACE` and `IMAGE_TAG` in this folder's `.env` file.
Copy `.env.sample` to `.env` if needed; `IMAGE_TAG` defaults to `latest`.
The commands below build images locally.

Build and start all three APIs from `src/HtmlToPdf`:

```powershell
docker compose up --build -d
```

The APIs are available at:

| Implementation | URL |
| --- | --- |
| YARP proxy | `http://localhost:8080` |
| Chrome CLI | `http://localhost:8081` |
| Playwright | `http://localhost:8082` |
| PuppeteerSharp | `http://localhost:8083` |

Use the proxy prefixes to select a converter. The prefix is removed before the
request is forwarded:

| Proxy request | Converter request |
| --- | --- |
| `POST /chrome/convert-file` | Chrome CLI `POST /convert-file` |
| `POST /playwright/convert-file` | Playwright `POST /convert-file` |
| `POST /puppeteer/convert-file` | PuppeteerSharp `POST /convert-file` |

For example:

```powershell
curl.exe -X POST `
  -F "file=@sample.html;type=text/html" `
  -o result.pdf `
  http://localhost:8080/playwright/convert-file
```

The corresponding backend health endpoints are available at `/chrome/health`,
`/playwright/health`, and `/puppeteer/health`. The proxy's own health endpoint
is `/health`.

View their logs or stop and remove the containers with:

```powershell
docker compose logs -f
docker compose down
```

### Build the API images

Run these commands from `src/HtmlToPdf`:

```powershell
docker build -t html-to-pdf-proxy .\HtmlToPdf.Proxy
docker build -t chrome-cli-api .\HtmlToPdf.Chrome.Api
docker build -t playwright-api .\HtmlToPdf.Playwright.Api
docker build -t puppeteer-sharp-api .\HtmlToPdf.PuppeteerSharp.Api
```

On Linux or macOS, replace `\` with `/`:

```bash
docker build -t html-to-pdf-proxy ./HtmlToPdf.Proxy
docker build -t chrome-cli-api ./HtmlToPdf.Chrome.Api
docker build -t playwright-api ./HtmlToPdf.Playwright.Api
docker build -t puppeteer-sharp-api ./HtmlToPdf.PuppeteerSharp.Api
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
  http://localhost:8080/convert-file
```

Linux or macOS:

```bash
curl -X POST \
  -F "file=@sample.html;type=text/html" \
  -o result.pdf \
  http://localhost:8080/convert-file
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

Google Chrome must be installed to run `HtmlToPdf.Chrome.Api` or
`HtmlToPdf.PuppeteerSharp.Api` locally:

```powershell
dotnet run --project .\HtmlToPdf.Chrome.Api
dotnet run --project .\HtmlToPdf.PuppeteerSharp.Api
```

For `HtmlToPdf.Playwright.Api`, build the project and install its matching
Chromium binary first:

```powershell
dotnet build .\HtmlToPdf.Playwright.Api\HtmlToPdf.Playwright.Api.csproj
pwsh .\HtmlToPdf.Playwright.Api\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet run --project .\HtmlToPdf.Playwright.Api
```

The local development port is printed by ASP.NET Core when the project starts.
Use that URL in place of `http://localhost:8080` in the examples above.

After starting all three converter APIs locally, run the proxy with:

```powershell
dotnet run --project .\HtmlToPdf.Proxy
```

Its default development configuration targets the HTTP ports in the converter
projects' launch profiles.

## Console applications

The solution also contains one-shot console converters. Their individual
documentation includes image build commands, output volume mounting, and local
browser setup:

- [`HtmlToPdf.Chrome.Cli/README.md`](HtmlToPdf.Chrome.Cli/README.md)
- [`HtmlToPdf.Playwright.Cli/README.md`](HtmlToPdf.Playwright.Cli/README.md)
- [`HtmlToPdf.PuppeteerSharp.Cli/README.md`](HtmlToPdf.PuppeteerSharp.Cli/README.md)

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
