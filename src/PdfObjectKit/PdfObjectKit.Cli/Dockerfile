FROM node:22-bookworm-slim AS visual
WORKDIR /visual
COPY PdfObjectKit.Cli/visual/package*.json ./
RUN npm ci --omit=dev --no-audit --no-fund
COPY PdfObjectKit.Cli/visual/visual.mjs ./

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY PdfObjectKit/PdfObjectKit.csproj PdfObjectKit/
COPY PdfObjectKit.Application/PdfObjectKit.Application.csproj PdfObjectKit.Application/
COPY PdfObjectKit.Cli/PdfObjectKit.Cli.csproj PdfObjectKit.Cli/
RUN dotnet restore PdfObjectKit.Cli/PdfObjectKit.Cli.csproj
COPY . ./
RUN dotnet publish PdfObjectKit.Cli/PdfObjectKit.Cli.csproj -c Release -o /app/publish --no-restore -p:GeneratePackageOnBuild=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble AS final
WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=visual /usr/local/bin/node /usr/local/bin/node
COPY --from=visual /visual /app/visual
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/PdfObjectKit.Cli.dll"]
