using System.ComponentModel;
using Microsoft.AspNetCore.Http.Features;
using ExcelToPdf.LibreOffice;

const long MaxFileSize = 10 * 1024 * 1024;
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MaxFileSize + (1024 * 1024);
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxFileSize + (1024 * 1024);
});
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapPost("/convert", async (IFormFile file, CancellationToken cancellationToken) =>
{
    if (file.Length == 0 || file.Length > MaxFileSize)
    {
        return Results.BadRequest("Upload a non-empty file no larger than 10 MB.");
    }

    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (extension is not (".xlsx" or ".xls"))
    {
        return Results.BadRequest("Upload a .xlsx or .xls file.");
    }

    try
    {
        await using var input = file.OpenReadStream();
        var pdf = await LibreOfficeConverter.ConvertAsync(input, extension, cancellationToken);
        var downloadName = Path.GetFileNameWithoutExtension(file.FileName.Replace('\\', '/')) + ".pdf";
        return Results.File(pdf, "application/pdf", downloadName);
    }
    catch (TimeoutException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (InvalidDataException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (Win32Exception)
    {
        return Results.Problem("LibreOffice is unavailable. Install it or configure LIBREOFFICE_BIN.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.DisableAntiforgery()
.WithName("ConvertExcelToPdf");
app.Run();
