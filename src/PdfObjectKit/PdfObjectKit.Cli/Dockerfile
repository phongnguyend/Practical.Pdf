FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY PdfObjectKit/PdfObjectKit.csproj PdfObjectKit/
COPY PdfObjectKit.Cli/PdfObjectKit.Cli.csproj PdfObjectKit.Cli/
RUN dotnet restore PdfObjectKit.Cli/PdfObjectKit.Cli.csproj
COPY . ./
RUN dotnet publish PdfObjectKit.Cli/PdfObjectKit.Cli.csproj -c Release -o /app/publish --no-restore -p:GeneratePackageOnBuild=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble AS final
WORKDIR /app
COPY --from=build /app/publish ./
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/PdfObjectKit.Cli.dll"]
