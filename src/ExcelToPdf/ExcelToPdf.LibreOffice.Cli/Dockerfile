FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY ExcelToPdf.LibreOffice.Cli/ExcelToPdf.LibreOffice.Cli.csproj ExcelToPdf.LibreOffice.Cli/
RUN dotnet restore ExcelToPdf.LibreOffice.Cli/ExcelToPdf.LibreOffice.Cli.csproj
COPY ExcelToPdf.LibreOffice.Cli/ ExcelToPdf.LibreOffice.Cli/
COPY ExcelToPdf.LibreOffice.Api/LibreOfficeConverter.cs ExcelToPdf.LibreOffice.Api/
RUN dotnet publish ExcelToPdf.LibreOffice.Cli/ExcelToPdf.LibreOffice.Cli.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends libreoffice-calc libreoffice-writer fonts-dejavu-core fonts-liberation \
    && rm -rf /var/lib/apt/lists/*
ENV LIBREOFFICE_BIN=/usr/bin/soffice
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ExcelToPdf.LibreOffice.Cli.dll"]
