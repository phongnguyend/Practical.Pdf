# Playwright HTML-to-PDF Converter

This project uses Playwright and its bundled Chromium browser to download a web
page and convert it to PDF. The Docker image pins Playwright 1.61.0 to match the
NuGet package used by the project and includes the .NET 10 runtime.

## Prerequisites

- Docker Desktop or Docker Engine must be installed and running.
- The container needs internet access to download the page being converted.

## Build the image

From the `HtmlToPdf` directory, run:

```powershell
docker build -t playwright-converter .\PlaywrightConverter
```

Linux or macOS:

```bash
docker build -t playwright-converter ./PlaywrightConverter
```

## Run the converter

Create a local output directory:

```powershell
New-Item -ItemType Directory -Force output
```

Run the image and pass the PDF output path after the image name:

```powershell
docker run --rm --init --ipc=host `
  -v "${PWD}\output:/output" `
  playwright-converter /output/playwright.pdf
```

Linux or macOS:

```bash
mkdir -p output
docker run --rm --init --ipc=host \
  -v "$(pwd)/output:/output" \
  playwright-converter /output/playwright.pdf
```

The generated file is written to `output/playwright.pdf`. If the output
argument is omitted, the converter writes `output/abc.pdf`.

The current sample converts `https://github.com/phongnguyend`. To change the
page, update the URL in `Program.cs` and rebuild the image.

## Run locally

Build the project so that Playwright generates its installation script:

```powershell
dotnet build .\PlaywrightConverter\PlaywrightConverter.csproj
```

Install the Chromium version matching the project's Playwright package:

```powershell
pwsh .\PlaywrightConverter\bin\Debug\net10.0\playwright.ps1 install chromium
```

Then run the converter:

```powershell
New-Item -ItemType Directory -Force output
dotnet run --project .\PlaywrightConverter -- .\output\playwright.pdf
```

Run the installation script again whenever the `Microsoft.Playwright` package
version changes. Installed browsers are reused from Playwright's browser cache.

## Troubleshooting

- Keep the Playwright Docker image version in `Dockerfile` synchronized with
  the `Microsoft.Playwright` package version in the project file.
- Playwright recommends `--init` to manage child processes and `--ipc=host` to
  prevent Chromium from running out of shared memory.
- If no PDF appears, confirm that the output directory is writable and that the
  container can access the internet.
