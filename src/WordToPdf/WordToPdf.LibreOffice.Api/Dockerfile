FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY WordToPdf.LibreOffice.Api/WordToPdf.LibreOffice.Api.csproj WordToPdf.LibreOffice.Api/
RUN dotnet restore WordToPdf.LibreOffice.Api/WordToPdf.LibreOffice.Api.csproj
COPY WordToPdf.LibreOffice.Api/ WordToPdf.LibreOffice.Api/
RUN dotnet publish WordToPdf.LibreOffice.Api/WordToPdf.LibreOffice.Api.csproj -c Release -o /app/publish --no-restore

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
ENTRYPOINT ["dotnet", "WordToPdf.LibreOffice.Api.dll"]
