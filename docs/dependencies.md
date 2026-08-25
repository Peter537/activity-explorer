# Dependency and license inventory

This inventory applies to Activity Explorer 0.1.0. NuGet versions are centrally pinned in `Directory.Packages.props`, every project commits `packages.lock.json`, and restore runs in locked mode. The local SDK baseline and container images are managed independently.

Activity Explorer source is MIT licensed. Dependencies and data providers retain their own terms.

| Dependency | Pinned version | Purpose | License / terms |
| --- | ---: | --- | --- |
| .NET SDK (local baseline) | 10.0.303 | Local build toolchain | MIT |
| .NET SDK (Docker build) | 10.0.400 | Container build toolchain | MIT |
| ASP.NET Core runtime/container | 10.0.11 | Blazor web runtime | MIT |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.11 | Web integration testing | MIT |
| Entity Framework Core SQLite | 10.0.11 | ORM and schema creation | MIT |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.11 | Hosted-service contracts | MIT |
| Garmin.FIT.Sdk | 21.212.0 | FIT decoding | Garmin FIT Protocol License |
| NetTopologySuite | 2.6.0 | WKB geometry | BSD-3-Clause |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Native SQLite bundle | Apache-2.0; SQLite is public domain |
| MapLibre GL JS | 6.2.0 | Locally served ESM map renderer | BSD-3-Clause |
| Microsoft.Testing.Platform | 2.3.3 | .NET 10 test runner | MIT |
| xunit.v3.mtp-v2 | 3.2.2 | Test framework with MTP v2 integration | Apache-2.0 |
| coverlet.MTP | 10.0.1 | MTP-native coverage collection | MIT |
| Microsoft.Playwright | 1.61.0 | Browser regression tests | MIT |
| OpenFreeMap | Explicit online opt-in | Style/tile service | Provider and source-data terms |
| OpenStreetMap data | Online opt-in only | Basemap data | ODbL; attribution required |

## Immutable delivery pins

The Docker build uses these multi-architecture image digests:

- `mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c`
- `mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94`

## MapLibre provenance

The vendored ESM bundle, worker, shared module, source maps, CSS, and license came from the official `maplibre-gl@6.2.0` npm tarball. `src/ActivityExplorer.Web/wwwroot/vendor/MAPLIBRE-PROVENANCE.json` records the source URL, retrieval time, npm SHA-512 integrity value, tarball SHA-512, and a SHA-256 for each retained file. Do not replace these files without regenerating and reviewing that metadata.

Blank map mode is the default and makes no OpenFreeMap request. When online maps are enabled, visible OpenStreetMap attribution must remain. Changing the configured style requires a fresh privacy, attribution, CSP, and provider-terms review.

## Garmin FIT SDK exception

Garmin publishes the official SDK through NuGet and its FIT developer documentation:

- <https://developer.garmin.com/fit/get-the-sdk/>
- <https://www.nuget.org/packages/Garmin.FIT.Sdk/21.212.0>
- <https://github.com/garmin/fit-csharp-sdk>

The package is governed by Garmin's FIT Protocol License rather than this repository's MIT license. The repository does not vendor or redistribute Garmin SDK source; restore obtains the package from NuGet. Anyone building or distributing Activity Explorer must review and comply with Garmin's current terms.

## Verification

Release verification uses:

~~~powershell
dotnet restore ActivityExplorer.slnx --locked-mode
./scripts/verify-package-health.ps1
~~~

The package-health script fails for known vulnerable or deprecated direct and transitive packages, verifies every recorded MapLibre hash, and rejects unlisted vendored runtime assets. Local release verification additionally builds with Recommended .NET analyzers, treats warnings as errors, verifies formatting, enforces coverage, runs browser tests, and smoke-tests the container as described in [Testing](testing.md).

This document is an engineering inventory, not legal advice.
