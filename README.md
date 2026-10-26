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
for each image. Branch and pull-request builds tag images locally with `:ci`.
Release-tag runs publish the same built images to Docker Hub.

### Release Docker images

Configure these repository **Actions secrets**:

- `DOCKERHUB_USERNAME`: the Docker Hub account used to sign in.
- `DOCKERHUB_TOKEN`: a Docker Hub access token with write access to the target repositories.

Optionally set the **Actions variable** `DOCKERHUB_NAMESPACE` to a Docker Hub
organization. It defaults to `DOCKERHUB_USERNAME`; the account must have write
access to repositories in that namespace.

Following the release-tag convention in `pdfobjectkit.yml`, push one of:

| Solution | Git tag example | Published image example |
| --- | --- | --- |
| HtmlToPdf | `releases/htmltopdf/v1.2.3` | `<namespace>/html-to-pdf-playwright-api:1.2.3` |
| ExcelToPdf | `releases/exceltopdf/v1.2.3` | `<namespace>/excel-to-pdf-libreoffice-api:1.2.3` |
| WordToPdf | `releases/wordtopdf/v1.2.3` | `<namespace>/word-to-pdf-libreoffice-api:1.2.3` |

```powershell
git tag releases/htmltopdf/v1.2.3
git push origin releases/htmltopdf/v1.2.3
```

Each release publishes every image in that solution's workflow matrix, including
engine APIs, CLIs, proxy, and playground. Repository names replace the slash in
the matrix image name with a hyphen: `html-to-pdf/proxy` becomes
`<namespace>/html-to-pdf-proxy:1.2.3`. Images target Linux AMD64.
Prereleases such as `v1.2.3-rc.1` publish the `1.2.3-rc.1` image tag.
Only the explicit version tag is published; `latest` is not updated.
Use a new version tag for each release. A failed matrix job can be rerun to
finish publishing that version. Manual dispatch on a release tag also publishes;
manual dispatch on a branch only builds.

For local .NET and service-image builds, run these commands from the relevant
solution folder (replace `HtmlToPdf` with `ExcelToPdf` or `WordToPdf`):

```powershell
dotnet build HtmlToPdf.slnx --configuration Release
docker compose build
```

Compose builds service images; the workflows additionally build CLI images.
