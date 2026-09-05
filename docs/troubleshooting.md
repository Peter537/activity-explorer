# Troubleshooting

## The SDK version is not available

`global.json` prefers .NET SDK 10.0.303. With its `feature` roll-forward policy, the .NET CLI can use a newer patch in the 10.0.3xx band or a later installed .NET 10 feature band when 10.0.303 is unavailable.

~~~powershell
dotnet --info
~~~

Install .NET SDK 10.0.303, a newer 10.0.3xx patch, or a later .NET 10 feature band from Microsoft, then rerun restore.

## NuGet restore fails

The Garmin FIT parser and .NET libraries are restored from NuGet. Confirm NuGet is reachable and no corporate proxy is replacing certificates:

~~~powershell
dotnet nuget list source
dotnet restore ActivityExplorer.slnx --disable-parallel
~~~

Review Garmin's FIT Protocol License before restoring/building. The repository intentionally does not vendor the SDK.

## Port 8342 is in use

Use a different loopback URL:

~~~powershell
dotnet run --project src/ActivityExplorer.Web -- --urls http://127.0.0.1:8343
~~~

Do not change this to 0.0.0.0 unless you have designed and added authentication and authorization.

## Buttons do nothing or the Blazor runtime is missing

Interactive controls require the local Blazor runtime. This request must return HTTP 200:

~~~powershell
curl.exe --fail http://localhost:8342/_framework/blazor.web.js --output NUL
~~~

If it returns 404, rebuild and restart the Web host from the current source. Version 0.1.0 maps .NET static-web-asset endpoints in addition to serving ordinary files from wwwroot.

An antiforgery message saying that a key is missing can occur after intentionally changing `ACTIVITY_EXPLORER_DATA` or deleting the previous data root while an old tab is open. Hard-refresh the tab once. Activity Explorer scopes its antiforgery cookie to the data root, so permanent and smoke-test key rings do not reuse each other's tokens. Never delete individual files from `keys/` during normal operation.

## Import remains queued

Keep the host running, check **Imports**, check free disk space, and restart once. A queued/interrupted import with a surviving staged file is recovered. Inspect bounded logs under logs/. Only one import is processed at a time by design.

## Archive is rejected

The archive may contain an unsafe path, symbolic link, excessive nesting, too many entries, or too much expanded data. Do not disable protections for an untrusted archive. Request a fresh official export and confirm it opens with a reputable local archive viewer.

Unknown layouts can be reported with a fictional/minimized reproduction. Never attach a real export to a public issue.

## Activity was skipped

Version 0.1.0 supports Cycling, Running, Walking, and Rowing. Unsupported sports are skipped intentionally. A file also needs a usable timestamp and enough data to be represented.

If an equivalent activity already exists for the same profile, the import enriches provenance rather than increasing the count.

## Charts are empty, labels are missing, or an older chart still looks wrong

**Not recorded in the source** is intentional when a sensor has no FIT samples; Activity Explorer does not infer power or other missing data. A missing or corrupt source must be exported again and reimported manually.

Populated charts in current builds show labelled axes and gridlines and expose retained point values through pointer and keyboard inspection. Activity and effort streams use real elapsed-time or distance positions, preserve recording gaps, and downsample dense series by bucket extrema. If a populated chart has no axis labels or direct pointer values, rebuild and restart the Web host, then hard-refresh the page. See [Understanding charts](charts.md) for the summary, synchronization, and missing-data rules. A refresh does not manufacture absent source fields.

## Map is blank

A blank background is the privacy-preserving default. If tracks are visible, the map is working. Enable the global OpenFreeMap option under **Settings** only if external style/tile traffic is acceptable. If tracks are absent, select **All profiles**, clear filters, confirm GPS is present, and open the detail page.

MapLibre is served locally. If the map canvas itself does not load, confirm the vendor assets were included in the publish/container output and inspect the browser console. Current builds normalize MapLibre's repeated-world bounds; `BadHttpRequestException: Map bounds contain an invalid latitude or longitude range` during ordinary navigation indicates that an older Web build is still running and should be rebuilt and restarted.

## Database schema is incompatible

Activity Explorer creates the current schema for a fresh data root and reopens databases that already match it. Startup idempotently adds `SourceKind`, `SourceName`, and `SourceFormat` to the immediately preceding segment schema. Other pre-release schema upgrades remain unsupported. If startup reports a different missing table or column, stop every Activity Explorer process, copy the entire data root, choose a fresh `ACTIVITY_EXPLORER_DATA` directory, and reimport from your preserved originals or provider export.

Do not delete the old root if its originals are your only copy. Preserve the database, WAL/SHM files if present, `originals/`, `quarantine/`, and `keys/` together. A complete backup requires stopping Activity Explorer.

## Watched folder does not import

Use an existing absolute directory, confirm read access, wait for the writer to finish, keep files at the top level, and confirm the FIT/GPX/TCX/GZ/ZIP extension. Periodic reconciliation catches events missed by FileSystemWatcher.

## Docker data permissions

The final image runs as the .NET image's non-root application user. A named Docker volume is configured automatically. For a bind mount, grant that user read/write access without making the directory world-writable.

docker compose down keeps the volume. docker compose down -v destroys it; use -v only after a verified backup.
