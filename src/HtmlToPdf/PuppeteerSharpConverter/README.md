# PuppeteerSharp HTML-to-PDF Converter

This project uses PuppeteerSharp and Google Chrome in headless mode to download
a web page and convert it to PDF. The Docker image includes the .NET 10 runtime
and Google Chrome Stable.

## Prerequisites

- Docker Desktop or Docker Engine must be installed and running.
- The container needs internet access to download the page being converted.

## Build the image

From the `HtmlToPdf` directory, run:

```powershell
docker build -t puppeteer-sharp-converter .\PuppeteerSharpConverter
```

Linux or macOS:

```bash
docker build -t puppeteer-sharp-converter ./PuppeteerSharpConverter
```

The build installs Google Chrome Stable and verifies the installation by
running `google-chrome --version`.

## Run the converter

Create a local output directory:

```powershell
New-Item -ItemType Directory -Force output
```

Run the image and pass the PDF output path after the image name:

```powershell
docker run --rm --init `
  -v "${PWD}\output:/output" `
  puppeteer-sharp-converter /output/puppeteer-sharp.pdf
```

Linux or macOS:

```bash
mkdir -p output
docker run --rm --init \
  -v "$(pwd)/output:/output" \
  puppeteer-sharp-converter /output/puppeteer-sharp.pdf
```

The generated file is written to `output/puppeteer-sharp.pdf`. If the output
argument is omitted, the converter writes `output/abc.pdf`.

The current sample converts `https://github.com/phongnguyend`. To change the
page, update the URL in `Program.cs` and rebuild the image.

## Verify Chrome in the image

```powershell
docker run --rm --entrypoint /usr/bin/google-chrome `
  puppeteer-sharp-converter --version
```

## Troubleshooting

- If Docker cannot connect to its daemon, start Docker Desktop or the Docker
  service and retry.
- If no PDF appears, confirm that the output directory is writable and that the
  container can access the internet.
- The image installs the `amd64` Google Chrome package and therefore targets
  x86-64 Docker hosts.
