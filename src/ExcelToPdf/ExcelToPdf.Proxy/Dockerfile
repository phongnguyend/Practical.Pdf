FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY ExcelToPdf.Proxy/ExcelToPdf.Proxy.csproj ExcelToPdf.Proxy/
RUN dotnet restore ExcelToPdf.Proxy/ExcelToPdf.Proxy.csproj
COPY ExcelToPdf.Proxy/ ExcelToPdf.Proxy/
RUN dotnet publish ExcelToPdf.Proxy/ExcelToPdf.Proxy.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ExcelToPdf.Proxy.dll"]
