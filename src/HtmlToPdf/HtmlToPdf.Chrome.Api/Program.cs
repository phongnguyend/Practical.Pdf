var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = FileConversionEndpoints.MaxHtmlFileSize + (1024 * 1024);
});

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapFileConversion();
app.MapUrlConversion();
app.Run();
