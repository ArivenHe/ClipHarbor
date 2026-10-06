FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY shared/ClipHarbor.Sync/ClipHarbor.Sync.csproj shared/ClipHarbor.Sync/
COPY server/ClipHarbor.Server/ClipHarbor.Server.csproj server/ClipHarbor.Server/
RUN dotnet restore server/ClipHarbor.Server/ClipHarbor.Server.csproj
COPY shared/ClipHarbor.Sync/ shared/ClipHarbor.Sync/
COPY server/ClipHarbor.Server/ server/ClipHarbor.Server/
RUN dotnet publish server/ClipHarbor.Server/ClipHarbor.Server.csproj -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG BUILD_COMMIT=development
ENV ASPNETCORE_HTTP_PORTS=8080 CLIPHARBOR_DATA_DIR=/data CLIPHARBOR_BUILD_COMMIT=$BUILD_COMMIT
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data && chown app:app /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ClipHarbor.Server.dll"]
