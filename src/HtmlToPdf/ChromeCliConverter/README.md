# Chrome CLI HTML-to-PDF Converter

This project uses the Google Chrome command-line interface in headless mode to
download a web page and convert it to PDF. The Docker image includes the .NET 10
runtime and Google Chrome Stable.

## Prerequisites

- Docker Desktop or Docker Engine must be installed and running.
- The container needs internet access to download the page being converted.

## Build the image

From the `HtmlToPdf` directory, run:

```powershell
docker build -t chrome-cli-converter .\ChromeCliConverter
```

On Linux or macOS, the equivalent command is:

```bash
docker build -t chrome-cli-converter ./ChromeCliConverter
```

The build installs Google Chrome Stable from Google's official Debian package
repository. It also runs `google-chrome --version` during the build so that the
build fails if Chrome was not installed successfully.

## Run the converter

Create a local directory for the generated PDF:

```powershell
New-Item -ItemType Directory -Force output
```

Run the image and mount that directory into the container:

```powershell
docker run --rm -v "${PWD}\output:/output" chrome-cli-converter
```

On Linux or macOS:

```bash
mkdir -p output
docker run --rm -v "$(pwd)/output:/output" chrome-cli-converter
```

The generated file is written to:

```text
output/abc.pdf
```

The current sample converts `https://github.com/phongnguyend`. To change the
page, update the URL in `Program.cs` and rebuild the image.

## Pass the output path as an argument

Pass the output path after the image name. The path must point inside the
mounted `/output` directory:

```powershell
docker run --rm `
  -v "${PWD}\output:/output" `
  chrome-cli-converter /output/github-profile.pdf
```

Linux or macOS:

```bash
docker run --rm \
  -v "$(pwd)/output:/output" \
  chrome-cli-converter /output/github-profile.pdf
```

If the argument is omitted, the converter writes `abc.pdf` to its working
directory, which is `/output` in the Docker image.

## Verify Chrome in the image

```powershell
docker run --rm --entrypoint /usr/bin/google-chrome `
  chrome-cli-converter --version
```

## Troubleshooting

- If Docker cannot connect to its daemon, start Docker Desktop or the Docker
  service and retry.
- If no PDF appears, confirm that the mounted output directory is writable and
  that the container can access the internet.
- The image installs the `amd64` Google Chrome package and therefore targets
  x86-64 Docker hosts.
