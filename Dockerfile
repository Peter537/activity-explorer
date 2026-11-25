FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS build
WORKDIR /source

COPY . .
RUN dotnet restore src/ActivityExplorer.Web/ActivityExplorer.Web.csproj --locked-mode

RUN dotnet publish src/ActivityExplorer.Web/ActivityExplorer.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false \
    -p:Version=0.1.0

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:aac50ceef10391e78a899e3d2ccd00bbafa2dcb56ccf4586cbccf00edb84fc21 AS final
LABEL org.opencontainers.image.title="Activity Explorer" \
      org.opencontainers.image.description="Local, file-first activity analytics" \
      org.opencontainers.image.version="0.1.0" \
      org.opencontainers.image.source="https://github.com/Peter537/activity-explorer" \
      org.opencontainers.image.licenses="MIT AND LicenseRef-Garmin-FIT-Protocol"

WORKDIR /app
ENV Urls=http://0.0.0.0:8342 \
    ASPNETCORE_HTTP_PORTS= \
    ACTIVITY_EXPLORER_DATA=/data \
    DOTNET_EnableDiagnostics=0
RUN mkdir /data && chown -R $APP_UID:$APP_UID /data
USER $APP_UID
EXPOSE 8342
VOLUME ["/data"]
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
ENTRYPOINT ["dotnet", "ActivityExplorer.Web.dll"]
