FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /source

COPY . .
RUN dotnet restore ActivityExplorer.slnx --locked-mode

RUN dotnet publish src/ActivityExplorer.Web/ActivityExplorer.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false \
    -p:Version=0.1.0

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS final
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
