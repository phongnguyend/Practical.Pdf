FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY WordToPdf.Proxy/WordToPdf.Proxy.csproj WordToPdf.Proxy/
RUN dotnet restore WordToPdf.Proxy/WordToPdf.Proxy.csproj
COPY WordToPdf.Proxy/ WordToPdf.Proxy/
RUN dotnet publish WordToPdf.Proxy/WordToPdf.Proxy.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "WordToPdf.Proxy.dll"]
