# Dependency and license inventory

This inventory applies to Activity Explorer 0.1.0. NuGet versions are centrally pinned in `Directory.Packages.props`, every project commits `packages.lock.json`, and restore runs in locked mode. The SDK and container images are independently pinned.

Activity Explorer source is MIT licensed. Dependencies and data providers retain their own terms.

| Dependency | Pinned version | Purpose | License / terms |
| --- | ---: | --- | --- |
| .NET SDK | 10.0.302 | Build toolchain | MIT |
| ASP.NET Core runtime/container | 10.0.10 | Blazor web runtime | MIT |
| Entity Framework Core SQLite | 10.0.10 | ORM and schema creation | MIT |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.10 | Hosted-service contracts | MIT |
| Garmin.FIT.Sdk | 21.212.0 | FIT decoding | Garmin FIT Protocol License |
| NetTopologySuite | 2.6.0 | WKB geometry | BSD-3-Clause |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Native SQLite bundle | MIT; SQLite is public domain |
| MapLibre GL JS | 6.1.0 | Locally served ESM map renderer | BSD-3-Clause |
| Microsoft.NET.Test.Sdk | 18.8.1 | VSTest host | MIT |
| xunit.v3 | 3.2.2 | Test framework | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | VSTest adapter | Apache-2.0 |
| coverlet.collector | 10.0.1 | Coverage collection | MIT |
| Microsoft.Playwright | 1.61.0 | Browser regression tests | Apache-2.0 |
| OpenFreeMap | Explicit online opt-in | Style/tile service | Provider and source-data terms |
| OpenStreetMap data | Online opt-in only | Basemap data | ODbL; attribution required |

## Immutable delivery pins

The Docker build uses these multi-architecture image digests:

- `mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0`
- `mcr.microsoft.com/dotnet/aspnet:10.0.10@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b`

## MapLibre provenance

The vendored ESM bundle, worker, shared module, source maps, CSS, and license came from the official `maplibre-gl@6.1.0` npm tarball. `src/ActivityExplorer.Web/wwwroot/vendor/MAPLIBRE-PROVENANCE.json` records the source URL, retrieval time, npm SHA-512 integrity value, tarball SHA-512, and a SHA-256 for each retained file. Do not replace these files without regenerating and reviewing that metadata.

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

The package-health script fails for known vulnerable transitive packages or deprecated direct packages. Local release verification additionally builds with Recommended .NET analyzers, treats warnings as errors, verifies formatting, enforces coverage, runs browser tests, and smoke-tests the container as described in [Testing](testing.md).

This document is an engineering inventory, not legal advice.
