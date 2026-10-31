FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY PdfObjectKit/PdfObjectKit.csproj PdfObjectKit/
COPY PdfObjectKit.Application/PdfObjectKit.Application.csproj PdfObjectKit.Application/
COPY PdfObjectKit.Playground.Api/PdfObjectKit.Playground.Api.csproj PdfObjectKit.Playground.Api/
RUN dotnet restore PdfObjectKit.Playground.Api/PdfObjectKit.Playground.Api.csproj
COPY . ./
RUN dotnet publish PdfObjectKit.Playground.Api/PdfObjectKit.Playground.Api.csproj -c Release -o /app/publish --no-restore -p:GeneratePackageOnBuild=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "PdfObjectKit.Playground.Api.dll"]
