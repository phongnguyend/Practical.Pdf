FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY ExcelToPdf.LibreOffice.Api/ExcelToPdf.LibreOffice.Api.csproj ExcelToPdf.LibreOffice.Api/
RUN dotnet restore ExcelToPdf.LibreOffice.Api/ExcelToPdf.LibreOffice.Api.csproj
COPY ExcelToPdf.LibreOffice.Api/ ExcelToPdf.LibreOffice.Api/
RUN dotnet publish ExcelToPdf.LibreOffice.Api/ExcelToPdf.LibreOffice.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends libreoffice-calc libreoffice-writer fonts-dejavu-core fonts-liberation \
    && rm -rf /var/lib/apt/lists/*
ENV LIBREOFFICE_BIN=/usr/bin/soffice
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ExcelToPdf.LibreOffice.Api.dll"]
