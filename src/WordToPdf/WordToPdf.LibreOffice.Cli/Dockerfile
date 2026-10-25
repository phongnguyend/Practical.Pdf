FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY WordToPdf.LibreOffice.Cli/WordToPdf.LibreOffice.Cli.csproj WordToPdf.LibreOffice.Cli/
RUN dotnet restore WordToPdf.LibreOffice.Cli/WordToPdf.LibreOffice.Cli.csproj
COPY WordToPdf.LibreOffice.Cli/ WordToPdf.LibreOffice.Cli/
COPY WordToPdf.LibreOffice.Api/LibreOfficeConverter.cs WordToPdf.LibreOffice.Api/
RUN dotnet publish WordToPdf.LibreOffice.Cli/WordToPdf.LibreOffice.Cli.csproj -c Release -o /app/publish --no-restore

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
ENTRYPOINT ["dotnet", "WordToPdf.LibreOffice.Cli.dll"]
