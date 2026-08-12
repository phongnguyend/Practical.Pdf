FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY PlaywrightConverter.Api.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# Keep this tag synchronized with the Microsoft.Playwright package version.
FROM mcr.microsoft.com/playwright/dotnet:v1.61.0-noble AS final
ENV ASPNETCORE_HTTP_PORTS=8080

WORKDIR /app
COPY --from=build /app/publish ./

EXPOSE 8080
ENTRYPOINT ["dotnet", "PlaywrightConverter.Api.dll"]
