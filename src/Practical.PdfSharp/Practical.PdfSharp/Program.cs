using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

Console.WriteLine("Hello, World!");

void FillFormFields(Stream input, List<FieldOptions> data, Stream output)
{
    using var pdfDocument = PdfReader.Open(input);
    var pdfAcroForm = pdfDocument.AcroForm;
    if (pdfAcroForm.Elements.ContainsKey("/NeedAppearances"))
    {
        pdfAcroForm.Elements["/NeedAppearances"] = new PdfBoolean(true);
    }
    else
    {
        pdfAcroForm.Elements.Add("/NeedAppearances", new PdfBoolean(true));
    }

    foreach (var item in data)
    {
        var field = pdfAcroForm.Fields[item.Name];
        if (field == null)
        {
            continue;
        }

        var propertyValue = item.Value;

        if (field is PdfCheckBoxField)
        {
            var checkBox = field as PdfCheckBoxField;
            checkBox!.Checked = propertyValue == "1";
        }
        else if (field is PdfRadioButtonField)
        {
            var radioButton = field as PdfRadioButtonField;
            radioButton!.Value = propertyValue == "1" ? new PdfName("/Yes") : new PdfName("/No");
        }
        else if (field is PdfTextField)
        {
            field.Value = new PdfString(propertyValue, PdfStringEncoding.Unicode);
        }

        field.ReadOnly = item.ReadOnly;
    }

    int? imageOrientationDegree = null;
    var image = Convert.FromBase64String("");
    using (var ms = new MemoryStream(image))
    {
        InsertImage(pdfDocument, "ImageFieldName", ms, 1, imageOrientationDegree);
    }

    if (imageOrientationDegree.HasValue)
    {
        pdfDocument.Pages[1].Orientation = PageOrientation.Portrait;
    }

    pdfDocument.Save(output, false);
}

XPoint GetImagePosition(PdfDocument pdfDocument, string imageField, int pageNumber)
{
    var fields = pdfDocument.AcroForm.Fields;
    var imagePosition = (PdfTextField)fields[imageField];

    if (!imagePosition.HasKids)
    {
        var imageRect = (PdfArray)imagePosition.Elements["/Rect"];
        string x = imageRect.Elements[0].ToString();
        string y = imageRect.Elements[1].ToString();

        return new XPoint
        {
            X = double.Parse(x),
            Y = double.Parse(y),
        };
    }
    else
    {
        var signs = (PdfArray)imagePosition.Elements["/Kids"];

        var field = ((PdfReference)signs.Elements[pageNumber]).Value as PdfDictionary;
        var imageRect = (PdfArray)field.Elements["/Rect"];

        string x = imageRect.Elements[0].ToString();
        string y = imageRect.Elements[1].ToString();

        return new XPoint
        {
            X = double.Parse(x),
            Y = double.Parse(y),
        };
    }
}

void InsertImage(PdfDocument pdfDocument, string imageField, Stream imageStream, int pageNumber, int? imageOrientationDegree)
{
    double pageHeight = pdfDocument.Pages[pageNumber].Height;

    var position = GetImagePosition(pdfDocument, imageField, pageNumber);

    XGraphics gfx = XGraphics.FromPdfPage(pdfDocument.Pages[pageNumber]);
    XImage image = XImage.FromStream(imageStream);

    var x = position.X;
    var y = pageHeight - position.Y - image.PointHeight;

    if (imageOrientationDegree.HasValue)
    {
        gfx.RotateAtTransform(imageOrientationDegree.Value, new XPoint { X = x, Y = y });
    }

    gfx.DrawImage(image, x, y);
}

record FieldOptions(string Name, string Value, bool ReadOnly);