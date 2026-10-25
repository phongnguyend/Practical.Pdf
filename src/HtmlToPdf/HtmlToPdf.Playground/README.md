# HtmlToPdf.Playground

Choose **Upload file** or **Website URL**. URL mode accepts a full HTTP/HTTPS
address and posts JSON to `/api/{engine}/convert-url`. Each selected engine
loads the page directly and provides a separate PDF to view or download.
The converter must be able to reach the URL; it does not use your browser's
signed-in session. The existing multipart `/convert-file` upload flow remains available.

React 19 UI for uploading or dragging one HTML file (up to 10 MB), selecting
one or more of Chrome, Playwright, and PuppeteerSharp, and viewing or downloading
PDFs through HtmlToPdf.Proxy. Selected engines convert concurrently, each with
its own progress, error, and PDF links. When multiple engines are selected,
download filenames include the engine name so the results are easy to compare.

## Development

Requires Node.js 22.12+ and npm. From this directory:

```powershell
npm ci
npm run dev
```

Open http://localhost:5174. Start HtmlToPdf.Proxy and the selected converter
with their default launch profiles. Vite forwards `/api/*` to
`http://localhost:62623`, removing `/api`. To use the Compose backend, create
`.env.local` containing `HTML_TO_PDF_PROXY_URL=http://localhost:8080` and restart Vite.

The client posts multipart field `file` to `/api/{chrome|playwright|puppeteer}/convert-file`.
No CORS changes are required. Files are uploaded only when Convert is clicked.
Use inline assets or absolute URLs; relative assets and companion files are not
uploaded. Conversion times out after two minutes.

## Docker

Run `docker compose up --build -d` from the parent directory, then open
http://localhost:8084. Nginx serves the frontend and forwards `/api/*` to the proxy.
For other deployments, configure the web server with the same forwarding rule.

## Checks

```powershell
npm run build
npm run test:e2e
```

Browser tests use installed Google Chrome and mocked API responses. Set
`HTML_TO_PDF_LIVE_TEST=1` to also exercise the real proxy and Chrome API,
which must already be running.
