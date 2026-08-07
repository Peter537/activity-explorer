FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /source

COPY . .
RUN dotnet restore ActivityExplorer.slnx --locked-mode

RUN dotnet publish src/ActivityExplorer.Web/ActivityExplorer.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false \
    -p:Version=0.1.0

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS final
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
