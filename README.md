# Practical.Pdf

- [PdfObjectKit](src/PdfObjectKit/README.md): serialize the PDF object graph with Base64 streams and binary strings, then rebuild a PDF from JSON.
- [HtmlToPdf](src/HtmlToPdf/README.md): convert HTML files and webpage URLs using Chrome, Playwright, or PuppeteerSharp.
- [ExcelToPdf](src/ExcelToPdf/README.md): convert Excel workbooks using LibreOffice.
- [WordToPdf](src/WordToPdf/README.md): convert Word documents using LibreOffice.

## Build workflows

The independent [HtmlToPdf](.github/workflows/htmltopdf.yml),
[ExcelToPdf](.github/workflows/exceltopdf.yml), and
[WordToPdf](.github/workflows/wordtopdf.yml) GitHub Actions workflows run on
pushes and pull requests that change their solution folder or workflow file.
They can also be started manually from the Actions tab.

Each workflow restores and builds its entire solution in Release mode using
.NET 10, then builds all its Dockerfiles for Linux AMD64, including the CLI,
API, proxy, and React playground images. Docker builds use separate cache scopes
for each image. Images are tagged locally with `:ci`; the workflows do not
publish images or require registry credentials.

For local .NET and service-image builds, run these commands from the relevant
solution folder (replace `HtmlToPdf` with `ExcelToPdf` or `WordToPdf`):

```powershell
dotnet build HtmlToPdf.slnx --configuration Release
docker compose build
```

Compose builds service images; the workflows additionally build CLI images.
