FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY PlaywrightConverter.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble AS dotnet-runtime

# Keep this tag synchronized with the Microsoft.Playwright package version.
FROM mcr.microsoft.com/playwright/dotnet:v1.61.0-noble AS final
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet

WORKDIR /app
COPY --from=build /app/publish ./

WORKDIR /output
ENTRYPOINT ["dotnet", "/app/PlaywrightConverter.dll"]
