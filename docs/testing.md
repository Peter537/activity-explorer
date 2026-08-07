# Testing

The default test suite is deterministic and uses synthetic, fictional data. It makes no live Garmin, Strava, OpenFreeMap, or OpenStreetMap request. Browser regressions run only when explicitly enabled.

## Release verification

~~~powershell
dotnet restore ActivityExplorer.slnx --locked-mode
dotnet format ActivityExplorer.slnx --verify-no-changes --no-restore
dotnet build ActivityExplorer.slnx --configuration Release --no-restore -m:1 -p:BuildInParallel=false
dotnet test tests/ActivityExplorer.Tests/ActivityExplorer.Tests.csproj --configuration Release --no-build --no-restore -m:1 -p:BuildInParallel=false --settings tests/coverage.runsettings --collect "XPlat Code Coverage" --results-directory tests/ActivityExplorer.Tests/TestResults
./scripts/verify-coverage.ps1
./scripts/verify-package-health.ps1
~~~

The release gate is at least 80% line and 60% branch coverage after exclusions for Razor-generated code, build output, and compiler-generated members. The package check fails for known vulnerable transitive packages and deprecated direct packages.

## Browser regressions

Build Release, install the browser pinned by Microsoft.Playwright 1.61.0, then opt in to the isolated test:

~~~powershell
pwsh tests/ActivityExplorer.Tests/bin/Release/net10.0/playwright.ps1 install chromium
$env:ACTIVITY_EXPLORER_BROWSER_TESTS = "1"
dotnet test tests/ActivityExplorer.Tests/ActivityExplorer.Tests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~BrowserRegressionTests"
~~~

The browser harness launches a real loopback Kestrel process with an isolated data root. Install the pinned Chromium build before running it. Responsive coverage checks primary pages and representative detail routes at 320, 360, 375, 768, 1121, 1280, and 1920 CSS pixels for page-level horizontal overflow.

The browser matrix also verifies:

- a closed mobile sidebar has no focusable off-screen links, while open/close/Escape returns focus to the menu button;
- Activities and World Map query URLs survive refresh and browser Back/Forward, normalize invalid values, reset deterministically, and reset pagination after a profile change;
- the generated component stylesheet is loaded; Records uses a full-width, left-aligned semantic table at 768, 1121, 1280, and 1920 pixels, and its restacked rows retain result, profile, date, and coverage at 320, 360, and 375 pixels;
- `/records?scope=outdoor` survives refresh and browser Back/Forward, invalid scope values normalize to all training, and profile changes preserve scope;
- Import history starts collapsed, opens with 10 entries, reveals 10 more cumulatively, and remains readable with warning states and long expandable summaries;
- missing detail IDs show a recovery state instead of permanent loading;
- activity section jumps stay on the current detail route, exact-value sliders work from the keyboard, and detail-card spacing remains 20 pixels at 375, 768, 1280, and 1920 pixels;
- individual confirmation/cancel/success, stale-selection errors, selected-row deletion, and exact all-filtered snapshots run against synthetic data only, including proof that a later matching import is not swept into an existing confirmation;
- profile deletion remains disabled until the exact confirmation phrase matches;
- `prefers-reduced-motion: reduce` removes nonessential transitions;
- blank map mode makes no third-party request and non-editable maps render lines without editable point markers.

## Covered behavior

Coverage includes:

- official-SDK FIT parsing, indoor/outdoor/generic sub-sport classification, XML hardening, archive traversal/link/limit handling, and both Garmin uploaded-file folder families including nested archives;
- owner-scoped IDs, hashes, natural fingerprints, provenance enrichment, matching source-version updates, and explicit virtual classification;
- Cycling target order through 200 km, pause-continuous recorded-distance and geometry-fallback windows, exact interpolation, retained reset/timestamp/GPS/speed boundaries, achieved-only storage, independent all-training/outdoor winners, and missing-snapshot repair;
- cancellation/interrupted staging semantics, startup requeue/missing staging, watched-folder queue-failure cleanup, copy/activity-file/profile quarantine recovery, and profile deletion;
- invariant dashboard sparkline geometry under a synthetic comma-decimal culture, filtered-ID resolution, atomic stale-ID rejection, cross-profile activity deletion, shared-file preservation, route/segment unlinking, segment-rank repair, and record refresh;
- fresh SQLite schema creation, repeat startup against a current-schema database, abandoned-running-import recovery, and absence of migration history;
- route GPX provenance, coordinate validation, viewport pruning, full-world/repeated-world normalization, antimeridian bounds, and segment direction/repeated passes;
- antiforgery missing/invalid/valid flows, framework-managed no-cache headers, streamed oversize cleanup, loopback host filtering, CSP, and security headers;
- MapLibre blank-mode privacy, wide-world normalization, responsive Records and Imports layouts, record-scope and filter query-state recovery, successful bounded GeoJSON requests, reduced motion, and keyboard navigation in Playwright;
- deterministic route and segment map limits with EF row-limiting-without-order warnings promoted to test failures;
- deterministic formatting, zero-warning Recommended analyzer builds, locked dependencies, exact version pins, and local container smoke testing.

All fixtures are synthetic. Never commit real exports, locations, names, device identifiers, credentials, tokens, or health data.

## Manual smoke test

Use an isolated root and loopback port:

~~~powershell
$env:ACTIVITY_EXPLORER_DATA = (Join-Path (Get-Location) ".smoke-data")
dotnet run --project src/ActivityExplorer.Web --no-build -- --urls http://127.0.0.1:8343
~~~

Confirm `/_framework/blazor.web.js` returns 200, then check fictional profile/import flows, status updates, charts, route drawing, profile confirmation, keyboard controls, restart recovery, and blank/online map settings. Exercise 375, 768, 1121, 1280, and 1920 pixel viewports with realistic dense data, then check 200% text zoom, long labels and summaries, visible focus, reduced motion, map interaction, console warnings, and empty/error/destructive states. For a profile with qualifying paused rides, confirm achieved Cycling efforts from 80 km through 180 km appear in catalog order, elapsed times include pauses, and the unachieved 200 km target remains hidden. Stop the app before removing only the known `.smoke-data` directory.
