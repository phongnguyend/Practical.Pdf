using PdfObjectKit;
using QRCoder;

var document = PdfReader.Read(File.ReadAllBytes("bin/input.pdf"));

using var qrData = QRCodeGenerator.GenerateQrCode("https://github.com/phongnguyend", QRCodeGenerator.ECCLevel.Q);
using var qrCode = new PngByteQRCode(qrData);
var qrImageBytes = qrCode.GetGraphic(pixelsPerModule: 10);

PdfImageEditor.InsertImage(
    document, pageNumber: 1,
    imageBytes: qrImageBytes,
    x: 50, y: 600, width: 100, height: 100);

File.WriteAllBytes("bin/output.pdf", PdfWriter.Write(document));
