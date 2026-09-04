# Activity Explorer

[![Version](https://img.shields.io/badge/version-0.1.0-176B87)](https://github.com/Peter537/activity-explorer)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Blazor Web App](https://img.shields.io/badge/Blazor-Web%20App-512BD4?logo=blazor&logoColor=white)](https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

Activity Explorer is a local, open-source Blazor application for exploring your cycling, running, and walking history. It works from files you own - Garmin account exports, Strava bulk exports, and individual FIT, GPX, TCX, GZ, or ZIP files - without API credentials, web scraping, or automated access to Garmin or Strava.

Version **0.1.0** is deliberately local-first and has no login system. It binds to localhost by default and stores the database, imported originals, and logs outside the repository.

## What it does

- Imports official Garmin and Strava account exports and individual activity files.
- Provides official-history ZIP, bounded multi-file, and local-inbox workflows without accepting provider credentials.
- Keeps immutable copies of every distinct imported original.
- Deduplicates exact files and equivalent activities across formats while retaining provenance.
- Shows dashboards, searchable activities, axis-labelled and synchronized gap-aware sensor/respiration charts with pointer and keyboard inspection, rich FIT summaries, laps, records, routes, segments, and a combined world map.
- Deletes one activity, selected activities, or an exact snapshot of the current filtered results with an inline permanent-deletion confirmation.
- Supports separate local owner profiles and an "All profiles" aggregate view.
- Calculates ordered cycling, running, and walking distance bests, timed distance bests from 5 minutes through sport-specific multi-hour targets, 5-second through 2-hour power bests, and directional local segment efforts.
- Creates local segments from reviewed GPX, FIT segment/course, TCX, KML, and GeoJSON paths, with optional trimming and direction reversal.
- Uses locally vendored MapLibre with a blank basemap by default; OpenFreeMap is a persistent, explicit global opt-in.
- Watches optional local folders without moving or deleting the files in them.

Activity Explorer does not use the Strava API, Garmin Connect API, OAuth credentials, provider passwords, automated Connect access, or a proprietary segment catalog. Generic user-supplied path files create independent local definitions; they do not establish provider identity or synchronization. Automatic phone-to-app synchronization is not available in 0.1.0.

## Quick start

Prerequisites:

- [.NET SDK 10.0.303](https://dotnet.microsoft.com/download/dotnet/10.0) is preferred; `global.json` accepts a later .NET 10 feature band when that exact SDK is unavailable.
- An official account export or one or more FIT/GPX/TCX files.

Run:

~~~powershell
dotnet restore ActivityExplorer.slnx --locked-mode
dotnet run --project src/ActivityExplorer.Web
~~~

Open [http://localhost:8342](http://localhost:8342), create a profile, and open **Imports**. Choose **Complete Garmin history**, **Recent files**, or **Local inbox**. Work continues in a durable background queue, and reports remain available after completion.

On Bash shells, the same dotnet commands work unchanged.

## Docker Compose

Docker is optional:

~~~powershell
docker compose up --build
~~~

The compose configuration publishes only 127.0.0.1:8342 and stores application data in the activity-explorer-data volume. Stop it with docker compose down. Do not add -v unless you intentionally want Docker to delete the volume and all imported data.

## Data location

By default the application uses the operating system's local application-data folder:

- Windows: %LOCALAPPDATA%\Activity Explorer
- Linux: normally ~/.local/share/Activity Explorer
- macOS: the platform local application-data location returned by .NET

Set ACTIVITY_EXPLORER_DATA to use a different absolute location:

~~~powershell
$env:ACTIVITY_EXPLORER_DATA = "D:\ActivityExplorerData"
dotnet run --project src/ActivityExplorer.Web
~~~

The directory contains:

- activity-explorer.db: SQLite metadata and summaries.
- originals/: immutable, content-addressed activity files grouped by profile.
- staging/: temporary imports; interrupted work is retained for restart recovery.
- logs/: bounded rolling diagnostics.
- quarantine/: profile directories or unreferenced activity originals awaiting durable cleanup after database deletion.
- keys/: persistent ASP.NET Core data-protection keys; include them in a complete backup.

Back up the whole application-data directory while Activity Explorer is stopped. See [Data storage and privacy](docs/data-storage-and-privacy.md).

## Supported imports

| Input | Support |
| --- | --- |
| Garmin account-export ZIP | Documented uploaded-files layouts, nested archives, and irrelevant-wellness filtering |
| Strava bulk-export ZIP | Original files enriched from activities.csv |
| FIT | Official SDK parsing for timing, laps, track, sensors, respiration, temperature, calories, and training fields when recorded |
| GPX / TCX | Hardened streaming XML readers |
| GZ / ZIP | Safely expanded with traversal, symlink, nesting, count, and size limits |
| Local segment path | One GPX, FIT segment/course, TCX, KML, or GeoJSON path; reviewed sport, trim, direction, and tolerance |

Only cycling, running, and walking activities are imported. Indoor and virtual variants map to their base sport. Other sport files are reported and skipped. Segment path uploads are parsed into local geometry and then discarded; only the safe file name and normalized format remain as provenance.

See [Importing data](docs/imports.md) and [Legal and export guides](docs/legal-and-exports.md).

## Maps and privacy

MapLibre renders local activity, route, and segment geometry on a blank basemap by default. In this mode every map makes zero requests to external map hosts. You can explicitly enable OpenFreeMap for all maps under **Settings**; the choice persists locally. Online style and tile requests reveal the viewed area to the configured provider, but do not upload stored activity files or tracks.

OpenStreetMap attribution remains visible whenever the online basemap is active. Route and other map queries are viewport-bounded, including viewports that cross the antimeridian. See [Maps](docs/maps.md).

## Security boundary

This is a no-login personal application. Localhost binding is intentional.

Do not expose it to a LAN, reverse proxy, tunnel, or the public internet. Anyone who can reach the application can view location and health data, download imported originals, edit activities, and permanently delete activities or profiles.

Activity deletion uses an inline confirmation that names the affected activity or exact selected/filtered count. Profile deletion requires entering DELETE followed by the profile name. Both are destructive. A profile JSON export describes the local records, but it is not a full backup of original files.

## Repository layout

~~~text
src/
  ActivityExplorer.Core/            Domain models, DTOs, and contracts
  ActivityExplorer.Infrastructure/  SQLite, imports, calculations, background work
  ActivityExplorer.Web/             Blazor Interactive Server host and UI
tests/
  ActivityExplorer.Tests/           Unit and SQLite integration tests
docs/                               User, architecture, privacy, and legal guides
~~~

The design keeps EF entities out of Razor components and exposes testable services such as IActivityImporter, IActivityQueryService, ISegmentMatcher, IRouteService, and IStatisticsService.

## Development

~~~powershell
dotnet restore ActivityExplorer.slnx --locked-mode
dotnet format ActivityExplorer.slnx --verify-no-changes --no-restore
dotnet build ActivityExplorer.slnx --configuration Release --no-restore -m:1 -p:BuildInParallel=false
dotnet test tests/ActivityExplorer.Tests/ActivityExplorer.Tests.csproj --configuration Release --no-build --no-restore -m:1 -p:BuildInParallel=false
~~~

For a release candidate, also run the coverage gates, package advisory/deprecation check, isolated Playwright regressions, and loopback-only Docker smoke test documented in [Testing](docs/testing.md).

No default test performs a live Garmin or Strava request. Test data is synthetic and fictional.

- [Architecture](docs/architecture.md)
- [Understanding charts](docs/charts.md)
- [Records methodology](docs/records.md)
- [Segment methodology](docs/segments.md)
- [Testing](docs/testing.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Dependency and license inventory](docs/dependencies.md)

## Legal

The Activity Explorer source code is MIT licensed. Dependencies retain their own licenses. In particular, Garmin's official FIT SDK uses Garmin's separate FIT Protocol License and is restored from NuGet rather than redistributed in this repository; review that license before building or distributing the application. See [Dependency and license inventory](docs/dependencies.md).

Activity Explorer is an unofficial product and is not affiliated with, endorsed by, or sponsored by Garmin, Strava, OpenFreeMap, or OpenStreetMap. Product names are used only to identify compatible user-supplied export formats. No third-party logos, credentials, or tokens belong in this repository.
