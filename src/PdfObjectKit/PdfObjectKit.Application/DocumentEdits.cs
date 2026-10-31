using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfObjectKit.Signing;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Models;
using PdfObjectKit.Writers;

namespace PdfObjectKit.Application;

public static class DocumentEdits
{
    public static async Task<byte[]> Apply(PdfObjectDocument document, EditRequest edit,
        Func<string, int, Task<byte[]>> readAsset, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.Value is null || edit.Value.Length > 10000)
        {
            throw new ArgumentException("Provide an edit value of at most 10000 characters.");
        }
        if (edit.Values is not null && (edit.Values.Count > 100 || edit.Values.Any(value => value is null || value.Length > 10000)))
        {
            throw new ArgumentException("Provide at most 100 selection values, each at most 10000 characters.");
        }
        if (PdfFormExtractor.Extract(document).Any(field => field.IsSigned))
        {
            throw new NotSupportedException("This PDF contains a signature. Editing would invalidate it; signed-document editing is disabled.");
        }
        byte[]? signedBytes = null;
        if (edit.Kind == "metadata" && edit.Metadata is not null)
        {
            PdfMetadataEditor.Update(document, edit.Metadata);
        }
        else if (edit.Kind == "add-watermark" && edit.Watermark is not null)
        {
            PdfWatermarkEditor.AddText(document, edit.Watermark);
        }
        else if (edit.Kind == "update-watermark" && edit.Watermark is not null && edit.WatermarkId is not null)
        {
            PdfWatermarkEditor.UpdateText(document, edit.WatermarkId, edit.Watermark);
        }
        else if (edit.Kind == "remove-watermark" && edit.WatermarkId is not null)
        {
            PdfWatermarkEditor.Remove(document, edit.WatermarkId);
        }
        else if (edit.Kind == "rotate-page")
        {
            PdfPageEditor.RotatePage(document, edit.PageNumber, edit.Rotation);
        }
        else if (edit.Kind is "delete-page" or "duplicate-page" or "reorder-pages" or "insert-blank-page")
        {
            var assemblyOptions = new PdfObjectKit.Options.PdfPageAssemblyOptions
            {
                MaxPages = 100,
                MaxObjects = 250000,
                MaxNodes = 1000000,
                MaxEncodedStreamBytes = 64L * 1024 * 1024
            };
            document = edit.Kind switch
            {
                "insert-blank-page" => PdfPageEditor.InsertBlankPage(document, edit.PageNumber, assemblyOptions, cancellation),
                "reorder-pages" => PdfPageEditor.ReorderPages(document, edit.PageNumbers ?? throw new ArgumentException("Provide a page order."), assemblyOptions, cancellation),
                "duplicate-page" => PdfPageEditor.DuplicatePage(document, edit.PageNumber, assemblyOptions, cancellation),
                _ => PdfPageEditor.DeletePage(document, edit.PageNumber, assemblyOptions, cancellation)
            };
        }
        else if (edit.Kind == "delete-objects")
        {
            if (edit.Deletions is not { Count: > 0 and <= 1000 } deletions
                || deletions.Any(item => item.PageNumber != edit.PageNumber
                    || item.Kind is not ("delete-text-line" or "delete-image" or "delete-field" or "delete-annotation")
                    || (item.Kind == "delete-text-line" && item.Runs is not { Count: > 0 })
                    || (item.Kind == "delete-annotation" && item.AnnotationNumber <= 0)
                    || (item.Kind == "delete-image" && item.ImageNumber <= 0)
                    || (item.Kind == "delete-field" && string.IsNullOrEmpty(item.FieldName))))
            {
                throw new ArgumentException("Choose between 1 and 1000 valid objects on the same page to delete.");
            }

            // One text rewrite validates every source hash against the original streams.
            // Text removal retains image invocation order; remove images from last to first.
            // The request owns this document: any failure discards every change.
            var runs = deletions.Where(item => item.Kind == "delete-text-line").SelectMany(item => item.Runs!)
                .DistinctBy(run => (run.StreamObject, run.FormOccurrenceId, run.OperatorIndex, run.OperandIndex)).ToList();
            if (runs.Count > 0)
            {
                PdfTextEditor.DeleteText(document, edit.PageNumber, runs);
            }
            foreach (var number in deletions.Where(item => item.Kind == "delete-image").Select(item => item.ImageNumber).Distinct().OrderDescending())
            {
                PdfPageEditor.DeleteImage(document, edit.PageNumber, number);
            }
            foreach (var number in deletions.Where(item => item.Kind == "delete-annotation").Select(item => item.AnnotationNumber).Distinct().OrderDescending())
            {
                PdfAnnotationEditor.Delete(document, edit.PageNumber, number);
            }
            foreach (var fieldName in deletions.Where(item => item.Kind == "delete-field").Select(item => item.FieldName!).Distinct())
            {
                PdfFormEditor.DeleteField(document, fieldName);
            }
        }
        else if (edit.Kind is "add-comment" or "add-highlight" or "add-underline" or "add-strikeout")
        {
            var kind = edit.Kind switch
            {
                "add-comment" => PdfAnnotationKind.Comment,
                "add-highlight" => PdfAnnotationKind.Highlight,
                "add-underline" => PdfAnnotationKind.Underline,
                _ => PdfAnnotationKind.StrikeOut
            };
            PdfAnnotationEditor.Add(document, edit.PageNumber, kind, edit.Regions ?? [new(edit.X, edit.Y, edit.Width, edit.Height)], edit.Value);
        }
        else if (edit.Kind == "annotation")
        {
            PdfAnnotationEditor.UpdateContents(document, edit.PageNumber, edit.AnnotationNumber, edit.Value);
        }
        else if (edit.Kind == "delete-annotation")
        {
            PdfAnnotationEditor.Delete(document, edit.PageNumber, edit.AnnotationNumber);
        }
        else if (edit.Kind == "sign-field" && edit.FieldName is not null)
        {
            var certificateBytes = await readAsset("certificate", 1048576);
            try
            {
                using var certificate = X509CertificateLoader.LoadPkcs12(certificateBytes, edit.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
                signedBytes = PdfSigner.Sign(document, edit.FieldName, certificate, edit.Value);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(certificateBytes);
            }
        }
        else if (edit.Kind == "button-action" && edit.FieldName is not null)
        {
            PdfFormEditor.SetButtonAction(document, edit.FieldName, edit.Action);
        }
        else if (edit.Kind == "rich-text" && edit.FieldName is not null)
        {
            PdfFormEditor.SetRichText(document, edit.FieldName, edit.Value, edit.TextAlignment);
        }
        else if (edit.Kind == "delete-text-line" && edit.Runs is { Count: > 0 })
        {
            PdfTextEditor.DeleteText(document, edit.PageNumber, edit.Runs);
        }
        else if (edit.Kind == "move-text" && edit.Runs is { Count: > 0 })
        {
            PdfTextEditor.MoveText(document, edit.PageNumber, edit.Runs, edit.DeltaX, edit.DeltaY);
        }
        else if (edit.Kind == "move-image")
        {
            PdfImageEditor.MoveImage(document, edit.PageNumber, edit.ImageNumber, edit.DeltaX, edit.DeltaY);
        }
        else if (edit.Kind == "move-field" && edit.FieldName is not null)
        {
            PdfFormEditor.MoveWidget(document, edit.FieldName, edit.WidgetIndex, edit.DeltaX, edit.DeltaY);
        }
        else if (edit.Kind == "apply-image-ocr")
        {
            if (edit.OcrLines is null)
            {
                throw new ArgumentException("Provide OCR lines and a TrueType font up to 5 MB.");
            }
            var font = await readAsset("ocrFont", 5000000);
            PdfImageOcrEditor.Apply(document, edit.PageNumber, edit.ImageNumber, edit.OcrLines, font, edit.ReplaceOcrImage);
        }
        else if (edit.Kind == "add-text")
        {
            PdfPageEditor.InsertText(document, edit.PageNumber, edit.Value, edit.X, edit.Y, edit.FontSize);
        }
        else if (edit.Kind is "add-image" or "replace-image")
        {
            var image = await readAsset("image", 5 * 1024 * 1024);
            if (edit.Kind == "replace-image")
            {
                PdfImageEditor.ReplaceImage(document, edit.PageNumber, edit.ImageNumber, image, edit.ImageFit);
            }
            else
            {
                PdfImageEditor.InsertImage(document, edit.PageNumber, image, edit.X, edit.Y, edit.Width, edit.Height);
                PdfPageEditor.ConsolidateContent(document, edit.PageNumber);
            }
        }
        else if (edit.Kind == "delete-image")
        {
            PdfPageEditor.DeleteImage(document, edit.PageNumber, edit.ImageNumber);
        }
        else if (edit.Kind == "add-field" && edit.FieldName is not null)
        {
            switch (edit.FieldType)
            {
                case PdfFormFieldKind.Text:
                    if (edit.RichText)
                    {
                        PdfFormEditor.InsertRichTextField(document, edit.PageNumber, edit.FieldName, edit.Value, edit.X, edit.Y, edit.Width, edit.Height, edit.TextAlignment ?? PdfTextAlignment.Left);
                    }
                    else
                    {
                        PdfFormEditor.InsertTextField(document, edit.PageNumber, edit.FieldName, edit.Value, edit.X, edit.Y, edit.Width, edit.Height, edit.TextAlignment ?? PdfTextAlignment.Left, edit.Multiline, edit.MaxLength, edit.Password, edit.Comb, edit.FileSelect);
                    }
                    break;
                case PdfFormFieldKind.PushButton:
                    PdfFormEditor.InsertPushButton(document, edit.PageNumber, edit.FieldName, edit.Value, edit.X, edit.Y, edit.Width, edit.Height);
                    break;
                case PdfFormFieldKind.Signature:
                    PdfFormEditor.InsertSignatureField(document, edit.PageNumber, edit.FieldName, edit.X, edit.Y, edit.Width, edit.Height);
                    break;
                case PdfFormFieldKind.CheckBox:
                    if (edit.Value is not ("Yes" or "Off"))
                    {
                        throw new ArgumentException("Checkbox value must be Yes or Off.");
                    }
                    PdfFormEditor.InsertCheckBox(document, edit.PageNumber, edit.FieldName, edit.Value == "Yes", edit.X, edit.Y, edit.Width, edit.Height);
                    break;
                case PdfFormFieldKind.Choice:
                    if (edit.MultiSelect)
                    {
                        PdfFormEditor.InsertMultiSelectListBox(document, edit.PageNumber, edit.FieldName, edit.Choices ?? [], edit.Values ?? [], edit.X, edit.Y, edit.Width, edit.Height, edit.TextAlignment ?? PdfTextAlignment.Left);
                    }
                    else if (edit.ListBox)
                    {
                        PdfFormEditor.InsertListBox(document, edit.PageNumber, edit.FieldName, edit.Choices ?? [], edit.Value, edit.X, edit.Y, edit.Width, edit.Height, edit.TextAlignment ?? PdfTextAlignment.Left);
                    }
                    else
                    {
                        PdfFormEditor.InsertDropdown(document, edit.PageNumber, edit.FieldName, edit.Choices ?? [], edit.Value, edit.X, edit.Y, edit.Width, edit.Height, edit.TextAlignment ?? PdfTextAlignment.Left);
                    }
                    break;
                case PdfFormFieldKind.RadioButton:
                    PdfFormEditor.InsertRadioGroup(document, edit.PageNumber, edit.FieldName, edit.Choices ?? [], edit.Value, edit.X, edit.Y, edit.Width, edit.Height);
                    break;
                default:
                    throw new ArgumentException("Choose a supported form field type.");
            }
        }
        else if (edit.Kind == "button-caption" && edit.FieldName is not null)
        {
            PdfFormEditor.SetPushButtonCaption(document, edit.FieldName, edit.Value);
        }
        else if (edit.Kind == "rename-field" && edit.FieldName is not null)
        {
            PdfFormEditor.RenameField(document, edit.FieldName, edit.Value);
        }
        else if (edit.Kind == "delete-field" && edit.FieldName is not null)
        {
            PdfFormEditor.DeleteField(document, edit.FieldName);
        }
        else if (edit.Kind == "text" && edit.Run is not null)
        {
            PdfTextEditor.UpdateText(document, edit.PageNumber, edit.Run, edit.Value);
        }
        else if (edit.Kind == "field" && edit.FieldName is not null)
        {
            if (edit.Values is not null)
            {
                PdfFormEditor.SetValues(document, edit.FieldName, edit.Values, edit.TextAlignment);
            }
            else
            {
                PdfFormEditor.SetValue(document, edit.FieldName, edit.Value, edit.TextAlignment);
            }
        }
        else
        {
            throw new ArgumentException("Choose a supported edit kind and provide its required fields.");
        }

        return signedBytes ?? PdfWriter.Write(document);
    }
}
