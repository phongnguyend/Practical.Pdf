using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using QRCoder;

const string defaultInputPdf = "input.pdf";
const string defaultOutputPdf = "output.pdf";
const string defaultQrContent = "https://github.com/phongnguyend";

string inputPdf = args.ElementAtOrDefault(0) ?? defaultInputPdf;
string outputPdf = args.ElementAtOrDefault(1) ?? defaultOutputPdf;
string qrContent = args.ElementAtOrDefault(2) ?? defaultQrContent;

if (!File.Exists(inputPdf))
{
    Console.Error.WriteLine($"Input PDF not found: {inputPdf}");
    return 1;
}

using QRCodeData qrData = QRCodeGenerator.GenerateQrCode(qrContent, QRCodeGenerator.ECCLevel.Q);
using PdfDocument document = PdfReader.Open(inputPdf, PdfDocumentOpenMode.Modify);

const double qrSize = 50;
const double rightMargin = 90;
const double bottomMargin = 15;

foreach (PdfPage page in document.Pages)
{
    using XGraphics gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

    double x = page.Width.Point - qrSize - rightMargin;
    double y = page.Height.Point - qrSize - bottomMargin;

    DrawQrCode(gfx, qrData, x, y, qrSize);
}

int pageCount = document.PageCount;
document.Save(outputPdf);
Console.WriteLine($"Added a QR code to {pageCount} page(s): {outputPdf}");

return 0;

static void DrawQrCode(XGraphics gfx, QRCodeData qrData, double x, double y, double size)
{
    int moduleCount = qrData.ModuleMatrix.Count;
    double moduleSize = size / moduleCount;

    // The white background preserves QRCoder's quiet zone over existing footer content.
    gfx.DrawRectangle(XBrushes.White, x, y, size, size);

    for (int row = 0; row < moduleCount; row++)
    {
        for (int column = 0; column < moduleCount; column++)
        {
            if (qrData.ModuleMatrix[row][column])
            {
                gfx.DrawRectangle(
                    XBrushes.Black,
                    x + column * moduleSize,
                    y + row * moduleSize,
                    moduleSize,
                    moduleSize);
            }
        }
    }
}
