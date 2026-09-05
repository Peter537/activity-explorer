# Testing

The default test suite is deterministic and uses synthetic, fictional data. It makes no live Garmin, Strava, OpenFreeMap, or OpenStreetMap request. Browser regressions run only when explicitly enabled.

## Release verification

~~~powershell
dotnet restore ActivityExplorer.slnx --locked-mode
dotnet format ActivityExplorer.slnx --verify-no-changes --no-restore
dotnet build ActivityExplorer.slnx --configuration Release --no-restore -m:1 -p:BuildInParallel=false
dotnet test tests/ActivityExplorer.Tests/ActivityExplorer.Tests.csproj --configuration Release --no-build --no-restore --coverlet --results-directory tests/ActivityExplorer.Tests/TestResults
./scripts/verify-coverage.ps1
./scripts/verify-package-health.ps1
~~~

The test project uses Microsoft Testing Platform v2 and opts out of test-platform telemetry through `testconfig.json`. The release gate is at least 80% line and 60% branch coverage after exclusions for Razor-generated code, build output, and compiler-generated members. The package check fails for known vulnerable or deprecated direct and transitive packages and for mismatched or unlisted vendored MapLibre assets.

## Browser regressions

Build Release, install the browser pinned by Microsoft.Playwright 1.61.0, then opt in to the isolated test:

~~~powershell
pwsh tests/ActivityExplorer.Tests/bin/Release/net10.0/playwright.ps1 install chromium
$env:ACTIVITY_EXPLORER_BROWSER_TESTS = "1"
dotnet test tests/ActivityExplorer.Tests/ActivityExplorer.Tests.csproj --configuration Release --no-build --no-restore --filter-class ActivityExplorer.Tests.BrowserRegressionTests
~~~

The browser harness launches a real loopback Kestrel process with an isolated data root. Install the pinned Chromium build before running it. Responsive coverage checks primary pages and representative detail routes at 320, 360, 375, 768, 1121, 1280, and 1920 CSS pixels for page-level horizontal overflow.

The browser matrix also verifies:

- a closed mobile sidebar has no focusable off-screen links, while open/close/Escape returns focus to the menu button;
- Activities and World Map query URLs survive refresh and browser Back/Forward, normalize invalid values, reset deterministically, and reset pagination after a profile change;
- the generated component stylesheet is loaded; populated Records data keeps Distance, Timed distance, and Power sections in order, formats timed results as metric distance, preserves activity links and semantic columns, omits unachieved targets, and uses a full-width, left-aligned table at 768, 1121, 1280, and 1920 pixels whose restacked rows retain result, profile, date, and coverage at 320, 360, and 375 pixels;
- `/records?scope=outdoor` and `/records?scope=indoor` survive refresh and browser Back/Forward, invalid scope values normalize to all training, and profile changes preserve scope;
- the dashboard monthly-distance chart renders five readable month labels, kilometre ticks, gridlines, exact pointer values, and an independent keyboard slider and 12-row Month/Distance table without overflowing at representative desktop and mobile widths;
- Import history starts collapsed, opens with 10 entries, reveals 10 more cumulatively, and remains readable with warning states and long expandable summaries;
- missing detail IDs show a recovery state instead of permanent loading;
- activity section jumps stay on the current detail route; detailed streams render unit-labelled axes and gridlines, switch between elapsed time and distance, synchronize nearest-retained-sample pointer values across populated charts, retain independent keyboard sliders and data tables, and keep 20-pixel card spacing at 375, 768, 1280, and 1920 pixels;
- the route segment creator links distance-snapped keyboard controls, endpoint nudges, map selection, elevation highlighting, live directional metrics, exact trimmed/reversed persistence, missing-elevation fallback, and responsive reflow at 375, 768, 1121, 1280, and 1920 pixels;
- the activity segment entry opens a focused creator with a blank required name, hides source indices, maps visual GPS endpoints back to the exact activity stream, preserves reversal and provenance, and recovers from missing elevation, insufficient GPS, and missing activities at the same responsive widths;
- segment detail renders the persisted definition elevation against derived path distance at 375, 768, 1121, 1280, and 1920 pixels, colors it by gap-aware 50 metre local grade, exposes exact distance/elevation/grade values to pointer and keyboard users, links each inspection change to one non-refitting map-marker update, retains the truthful missing-elevation fallback, and applies the shared axis and inspection behavior to a selected effort;
- individual confirmation/cancel/success, stale-selection errors, selected-row deletion, and exact all-filtered snapshots run against synthetic data only, including proof that a later matching import is not swept into an existing confirmation;
- profile deletion remains disabled until the exact confirmation phrase matches;
- `prefers-reduced-motion: reduce` removes nonessential transitions;
- blank map mode makes no third-party request and non-editable maps render lines without editable point markers.

## Covered behavior

Coverage includes:

- rowing FIT imports with both Rowing and FitnessEquipment/IndoorRowing classifications, stroke totals, missing sensors, XML/Strava rowing labels, independent Indoor records, GPS-free fixed-distance benchmarks, and rowing pace/stroke-rate browser displays;
- official-SDK FIT parsing, indoor/outdoor/generic sub-sport classification, XML hardening, archive traversal/link/limit handling, and both Garmin uploaded-file folder families including nested archives;
- owner-scoped IDs, hashes, natural fingerprints, provenance enrichment, matching source-version updates, and explicit virtual classification;
- Cycling fixed-distance target order through 200 km; Cycling timed targets at 5, 10, 20, and 30 minutes and 1, 2, and 4 hours; Running and Walking timed targets at 5, 10, 15, and 30 minutes and 1 and 2 hours; pause-continuous recorded-distance and geometry-fallback windows; exact boundary interpolation; retained reset, timestamp, distance-data, GPS, and speed boundaries; achieved-only storage; independent all-training/indoor/outdoor winners; GPS-less indoor timed records; and idempotent version-6-to-version-7 snapshot backfill;
- cancellation/interrupted staging semantics, startup requeue/missing staging, watched-folder queue-failure cleanup, copy/activity-file/profile quarantine recovery, and profile deletion;
- invariant dashboard chart and axis geometry under a synthetic comma-decimal culture; full-series chart summaries, readable unit-aware ticks, nearest retained samples, gap-preserving extrema downsampling, and truthful empty distance-axis series; filtered-ID resolution, atomic stale-ID rejection, cross-profile activity deletion, shared-file preservation, route/segment unlinking, segment-rank repair, and record refresh;
- fresh SQLite schema creation, repeat startup against a current-schema database, idempotent segment-provenance and effort-metric compatibility upgrades that preserve legacy values, abandoned-running-import recovery, and absence of migration history;
- route GPX provenance, coordinate validation, viewport pruning, full-world/repeated-world normalization, antimeridian bounds, and segment direction/repeated passes;
- continuous segment alignment across uneven sampling, intermittent missing coordinates, overlapping endpoint zones, short paths, antimeridian crossings, and later spatially overlapping returns without losing original stream indices; effort regressions cover fixed-distance speed ordering, 1-5 second timestamp weighting, zero and missing sensors, invalid intervals, finite maxima, legacy notices, stable-ID manual recomputation, recorded diagnostics, and a selected-effort distance axis rebased to zero; very long definitions use bounded alignment, and failed initial matching cannot persist a partial segment;
- strict global rendering budgets for alternating grade transitions and many short elevation runs, with representative gaps retained and no interpolation across missing elevation;
- antiforgery missing/invalid/valid flows, framework-managed no-cache headers, streamed oversize cleanup, loopback host filtering, CSP, and security headers;
- MapLibre blank-mode privacy, wide-world normalization, responsive Records, Imports, and route-creator layouts, record-scope and filter query-state recovery, successful bounded GeoJSON requests, reduced motion, and keyboard navigation in Playwright;
- deterministic route and segment map limits with EF row-limiting-without-order warnings promoted to test failures;
- deterministic formatting, zero-warning Recommended analyzer builds, locked dependencies, exact version pins, and local container smoke testing.

All fixtures are synthetic. Never commit real exports, locations, names, device identifiers, credentials, tokens, or health data.

## Manual smoke test

Use an isolated root and loopback port:

~~~powershell
$env:ACTIVITY_EXPLORER_DATA = (Join-Path (Get-Location) ".smoke-data")
dotnet run --project src/ActivityExplorer.Web --no-build -- --urls http://127.0.0.1:8343
~~~

Confirm `/_framework/blazor.web.js` returns 200, then check fictional profile/import flows, status updates, route drawing, profile confirmation, restart recovery, and blank/online map settings. For charts, verify the dashboard month/kilometre frame, switch activity and selected-effort streams between elapsed time and distance, compare synchronized pointer values, and open an independent keyboard slider and data table. Confirm metric units, labels, and tooltips remain readable with dense and missing data. Exercise 375, 768, 1121, 1280, and 1920 pixel viewports with realistic dense data, then check 200% text zoom, long labels and summaries, visible focus, reduced motion, map interaction, console warnings, and empty/error/destructive states. For a profile with qualifying paused rides, confirm achieved Cycling fixed-distance efforts from 80 km through 180 km appear in catalog order, elapsed times include pauses, and the unachieved 200 km target remains hidden. Confirm **Timed distance bests** follows **Distance bests**, reports metric distance for achieved time targets only, includes pauses in its exact elapsed windows, and accepts a GPS-less indoor stream only when it has valid per-point recorded distance. Stop the app before removing only the known `.smoke-data` directory.
