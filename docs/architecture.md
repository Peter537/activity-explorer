# Architecture

Activity Explorer 0.1.0 is one ASP.NET Core Blazor Web App using Interactive Server rendering. UI, background processing, SQLite, and managed files run in one local process. Separation is enforced through projects and service contracts, not network services.

~~~mermaid
flowchart LR
    Browser["Local browser"] -->|"antiforgery + streamed multipart"| Stage["Managed staging"]
    Folder["Optional watched folder"] --> Stage
    Stage --> Queue["Durable import queue"]
    Queue --> Parser["FIT / GPX / TCX / archive parsers"]
    Stage --> SegmentReader["Bounded segment-path reader"]
    SegmentReader --> LocalSegment["Reviewed trim + direction"]
    LocalSegment --> SQLite
    Parser --> Journal["File-operation journal"]
    Journal --> Originals["Verified managed originals"]
    Parser --> SQLite["SQLite metadata and streams"]
    SQLite --> UI["Blazor services and UI"]
    UI --> LocalMap["Local MapLibre ESM"]
    LocalMap -. "explicit global opt-in" .-> Online["OpenFreeMap style and tiles"]
~~~

## Projects

- `ActivityExplorer.Core` contains domain entities, DTOs, enums, track models, and service contracts, without EF Core or Blazor dependencies.
- `ActivityExplorer.Infrastructure` implements SQLite, imports, managed storage, calculations, maps, routes, segments, profiles, and hosted workers.
- `ActivityExplorer.Web` is the composition root, same-origin endpoint surface, security middleware, logging, locally vendored browser assets, and Razor UI.
- `ActivityExplorer.Tests` contains synthetic unit, SQLite integration, security-host, lifecycle, coverage, and isolated Playwright tests.

Razor components consume service DTOs; EF entities stay inside infrastructure.

## Reporting dates and totals

Core defines `ReportingPreset`, `ReportingDateSelection`, and `ResolvedOwnerPeriod`. `ReportingDates` resolves inclusive calendar dates in each owner's `ReportingTimeZone`, defaulting to `Europe/Copenhagen`, against one captured `TimeProvider` instant. The result contains visible dates and half-open UTC boundaries. Repeated midnight uses its earliest occurrence; skipped midnight uses the first valid instant. Unrepresentable boundaries are rejected. Stored timestamps and original source offsets remain unchanged.

`ActivityFilter` and `MapQuery` accept relative periods and a captured reference instant while preserving custom-date callers. Infrastructure's shared `ReportingDateQuery` applies owner/date predicates in SQL before pagination or map limits. `IActivityQueryService.SearchAsync` returns `ActivitySearchResult` with the page, complete `ActivityTotals`, and an `EffectiveFilter` containing resolved owner periods. Profile settings, summary aggregates, and page rows are read in one SQLite transaction. Totals use recorded distance, moving time, and ascent; queries neither read streams nor apply badge qualification rules. Generic `PagedResult<T>` is unchanged.

Activities and World Map share the date input component. Public URLs preserve relative preset slugs or fixed custom dates. Sorting, pagination, and deletion preparation reuse the displayed effective filter; applying filters, profile changes, and page reloads resolve periods again. Internal map activity requests carry the captured reference instant across viewport changes. Edited controls are separate from applied results, and superseded activity loads cannot replace newer results. Reporting uses existing summary fields and profile timezone settings without a schema migration.

## Activity organization

`IActivityOrganizationService` owns tag management, assignments, versioned saved criteria, and batch preparation/application. Tags and saved-search names use trimmed display spelling and unique owner/invariant-uppercase keys. Assignments have a composite activity/tag key and cascade only when their activity or tag is deleted. Razor consumes organization DTOs rather than EF entities.

Tag filters require one owner, validate every tag's ownership, and join the shared activity predicate before rows, complete totals, matching IDs, or deletion snapshots are resolved. Multiple tags match any selected tag. Saved criteria include search, sport, power, device, tag IDs and last-known names, sort, and reporting-date selection. They exclude pagination, captured clocks, resolved dates, and transient selections/reviews. Unsupported or malformed criteria fail visibly; missing tags require explicit repair and save.

Activities carry a zero-initialized integer mutation version. Metadata edits, manual metrics, imports/replacements, transfers, assignments, gear edits, and assigned-tag rename/deletion advance it under owner mutation locks. Derived record and segment recomputation does not. Imports and ordinary metadata updates preserve assignments; tag-only edits leave `UserEdited` unchanged.

Batch preparation returns copied, read-only collections of exact IDs, mutation versions, operations, and reviewed tag identities/names. Application holds the owner lock and one transaction, validates all members before writing, processes sets in chunks of 500, and commits once. Missing, moved, or changed members reject the whole operation. Gear changes use the existing trimmed 160-character field and set `UserEdited`; organization changes do not recalculate technical data. Cross-page selections remain within the same applied criteria and resolved reporting periods.

## Progress reporting

`DashboardQuery` and `TrainingCalendarQuery` use the existing activity query service. Both project stored summary fields after owner, sport, and owner-local reporting-date filtering. Profile settings, selected/comparison summaries, and dashboard record highlights are read in a consistent SQLite transaction. No stream, geometry, or original-file reads are needed. Daily grouping supplies totals, distinct active dates, Monday-start weeks, and calendar-month buckets. Calendar dates and activity-list links share Phase 1’s inclusive date contract.

Comparisons resolve calendar dates before converting them to UTC boundaries. Previous periods have equal inclusive day counts; prior-year dates clamp leap-day endpoints. An unbounded or unrepresentable comparison remains unavailable instead of implying zero. Aggregate DTOs carry resolved ranges so Razor does not reconstruct membership rules. Results are calculated on demand, without a schema change, persistent aggregate, or cross-request cache.

Benchmark progress uses `IStatisticsService.GetHistoryAsync` and shares qualifying-attempt evaluation with the strongest-first view. The selected benchmark and reporting dates filter candidate activities before batched stream reads in one consistent, deferred SQLite read snapshot. A replacement request can read while a cancelled calculation unwinds, without reserving SQLite's single writer. Each owner receives a chronological best-per-activity series and a running best reset at the beginning of the selected period. Whole-activity benchmarks stay summary-only. Cancellation and failures discard partial results, and UI load generations prevent stale profile/filter responses from replacing current results.

## Personal goals

Core's `IGoalService` exposes goal creation, listing, detail/history/contributors, definition edits, and archival through DTOs. `GoalService` derives editions and progress from stored activity summaries using the shared reporting-date predicate. `GoalCalendar` supplies pure calendar calculations; progress calculations use plain projected values. Every read captures one clock instant and reads owner settings, definitions, summaries, and contribution evidence in one deferred SQLite snapshot. It performs no writes and loads neither streams nor geometry.

`PersonalGoal` stores its owner, immutable metric/schedule, archive cutoff, and mutation version. `GoalDefinitionRevision` stores name, sport, target, and effective edition start, uniquely keyed by goal and canonical calendar start. Targets use metres, seconds, or counts. The first edition's visible start is clipped to the effective date without prorating its target; its identity is still the Monday, month start, or year start. Later editions use complete calendar periods. The latest applicable revision supplies each edition's definition.

Mutations hold the existing owner lock and recheck ownership, mutation version, expected edition key, and expected period state against one captured instant. This rejects both rollover and a form crossing the first start or one-off end. Current-only edits restore the prior template at the next boundary unless a scheduled revision already exists; future edits replace only the next revision. Before the first start, the initial definition remains editable. Ended definitions are immutable. Archival stores an exclusive edition cutoff, retaining an already-active edition's entire range; prestart archival prevents any edition from beginning.

History and contribution dates are paginated in groups of 20. Detail summary queries cover only the requested edition, displayed history, and editable current edition (at most 22 distinct windows), avoiding intervening skipped years. Every matching summary contributes, including future-dated entries inside the dates, and active-day evidence groups all matching activities under one local date. Achievement, period state, uncapped percentage, remaining amount, and active-only pace guides are derived on read. There is no achieved-state table, worker, or cross-request cache.

Goals stay with their owner when activities transfer; subsequent reads update both owners' progress. Profile deletion cascades goal definitions and revisions. The existing transactional profile export includes authoritative schedules, archive cutoffs, mutation versions, and ordered definition revisions, retaining schema version 1. The `/goals` list and dashboard follow profile selection; a detail route identifies one explicitly labelled owner. Superseded reads cannot replace newer profile or route results, and edition/pagination state lives in the detail URL. No new HTTP endpoint or dependency is required.

## Activity range inspection

Core's `ActivityRange` identifies endpoints through original source brackets, interpolation fractions, and timestamps. Stream-backed `RecordAttempt` and `ActivityDetail` DTOs include a SHA-256 fingerprint of the stream schema and compressed payload; summary-only benchmarks still avoid payload reads. Benchmark ordering, eligibility, and values retain their existing algorithms. Additional endpoint metadata preserves filtered-sample interpolation without treating its numeric position as an adjacent raw-sample interpolation.

`IActivityRangeAnalyzer` calculates full-source metrics and gap-separated map projections in Infrastructure. It reports available contributions, covered seconds, completeness, and distance method without persistence or external requests. The Web layer owns versioned URL serialization, validation recovery, and the selected interval. Query-only navigation reuses the loaded activity; superseded loads and calculations are cancelled. Chart hit testing uses source-axis data independently of downsampling, and chart/map highlights use supported source edges. The activity map projection is separate from the segment creator's existing integer index contract.

## Segment effort comparison

`ISegmentService.GetComparisonAsync` reads definition, owner and effort metadata, child candidates, and only the requested activity streams in one deferred SQLite snapshot. Each distinct selected activity is decoded once after releasing the snapshot. Exact effort IDs, ownership, sport, stream ownership, and source boundaries are validated before comparison; missing or invalid selections never fall back to another pass.

`SegmentComparisonCalculator` builds bounded, endpoint-anchored monotone correspondences to the ordered saved geometry. Core DTOs preserve canonical distance, original source brackets, actual elapsed time, gap-separated map runs, and explicit unavailable reasons. Child pairing maps contained pass boundaries through that correspondence and requires unique, ordered placements. The focused Web comparison component shares scales and formatting with existing charts while keeping its paired inspector separate from single-series interactions. Results are ephemeral: no comparison table, schema migration, HTTP endpoint, dependency, or stored effort-version change. See [Comparing two efforts](segments.md#comparing-two-efforts) for resolution, limits, and timing rules.

## Badge calculation

`IBadgeService` reads profile settings and activity summaries in one consistent SQLite read transaction, then passes plain DTOs to the pure `BadgeEvaluator`. `BadgeCatalog` defines stable family/tier identifiers, targets, recurrence, points, requirements, and local vector artwork keys. Calendar groups and shared contributions are reused across tiers; streams are not loaded. Results, evidence, points, and levels are calculated on demand without an award table, background worker, or cross-request cache.

The profile timezone and selected month determine the historical cutoff. Import order and source provenance do not affect an award's identity: profile, definition, and edition identify it. Every new read reflects committed imports, edits, transfers, and deletions. Profile timezone changes use the existing owner mutation lock and field. The Badges component cancels superseded requests and preserves query state in navigation. See [Badges and levels](badges.md) for the catalogue and qualification rules.

## Exploration calculation

Core's `IExplorationIndexService` exposes scoped status and cancellable first-use builds. `IExplorationQueryService` returns complete summaries, twelve-month discovery history, paginated cells/contributors, and bounded viewport groups. World Map consumes these DTOs; `/internal/map/exploration` serves the viewport response. The existing line service and its 2,000-activity limit remain separate.

`ExplorationGrid` assigns stable zoom-14 Web Mercator identities and visits valid samples plus cells crossed by eligible adjacent edges. It preserves antimeridian crossings, coordinate limits, recording breaks, timestamp gaps/resets, and recorded-distance resets. `ExplorationSourceReader` reads hash-verified originals to recover breaks only when the entire ordered GPS/time sequence exactly matches the canonical stream. Unverifiable continuity yields point-only visits with a persisted diagnostic. Neither calculation rewrites canonical streams or source files. The precise geometry and date rules are documented in [Maps](maps.md#recorded-paths-and-calculation-status).

`ActivityExplorationIndex` stores each activity's input/calculation versions, cell count, limited-coverage flag and diagnostic. `ActivityExplorationCell` stores unique activity/cell memberships; both cascade with the activity. `Activity.ExplorationInputVersion` advances for technical replacement and source-evidence association changes, independently of the general metadata mutation version. Ownership, sport, start time and reporting timezone are joined at query time rather than copied into this cache.

The singleton index service takes pending IDs in batches of 32 and serializes extraction one activity at a time. It reads a consistent source snapshot, releases it before decoding and original-file inspection, then acquires the owner mutation lock and revalidates ownership, input revision and source identity. Header and memberships commit atomically only for current inputs. Successful zero-cell activities receive checkpoints; missing GPS streams fail visibly. Cancellation and failure retain prior checkpoints. Rechecks replace only scoped limited results after successful extraction. There is no exploration startup worker or import-triggered build; the next feature visit resumes missing work after restart.

Queries use one deferred SQLite snapshot for current headers, owner settings, summaries and memberships, without decoding streams or reading originals. Completeness covers all activities in the owner/sport scope before date or viewport filtering. Missing or stale checkpoints suppress aggregate results. Range membership uses reporting boundaries; first/new visits inspect all scoped history, and All profiles counts a union of cells. Cumulative membership uses one inclusive owner-local date, with new cells restricted to first discoveries on that date. Monthly history buckets each union cell by its earliest reporting date. Complete counts and the frequency scale are independent of the viewport; rendering coarsens groups until at most 2,048 features remain. UI request generations and cancellation keep superseded profile/filter results from replacing current results.

## Import lifecycle

1. The browser obtains a no-store antiforgery token. A bounded multipart reader streams one upload to a `CreateNew` file under a unique staging directory. A watched-folder import copies a stable source into the same managed area.
2. An `ImportBatch` row commits before its identifier enters the single-reader in-process queue.
3. The processor acquires the owner mutation lock, marks the batch running, and selects a parser.
4. Archive contents expand only within the isolated staging tree under traversal, link, count, depth, and expanded-size limits.
5. Candidates are matched by owner plus provider ID, SHA-256, and natural fingerprint.
6. A journaled copy writes the content-addressed original, verifies its SHA-256, then coordinates provenance and activity commits.
7. Statistics and local segment efforts are recomputed.
8. Completed, completed-with-warning, and failed imports receive `CompletedAtUtc` and remove staging. Cancellation becomes `Interrupted`, keeps staging, and leaves completion empty.
9. Startup marks abandoned running work interrupted, requeues queued/interrupted batches with staging, fails missing staging clearly, and recovers incomplete journal phases idempotently.

Unknown pre-existing files under managed originals are retained and reported; they are never guessed to be orphans.

## Storage model

SQLite stores relational summaries, compressed point streams, provenance, global settings, and lifecycle journals. WKB geometry has numeric bounds; longitudes use a minimal circular interval so viewport queries can represent antimeridian crossings. Route, activity, and segment map responses are bounded and capped.

~~~mermaid
erDiagram
    OwnerProfile ||--o{ ImportBatch : owns
    ImportBatch ||--o{ SourceFile : records
    OwnerProfile ||--o{ Activity : owns
    OwnerProfile ||--o{ Tag : owns
    OwnerProfile ||--o{ SavedSearch : owns
    OwnerProfile ||--o{ PersonalGoal : owns
    PersonalGoal ||--|{ GoalDefinitionRevision : defines
    Activity ||--o{ ActivityTag : has
    Tag ||--o{ ActivityTag : labels
    OwnerProfile ||--o{ Route : owns
    OwnerProfile ||--o{ Segment : owns
    Activity ||--o| ActivityStream : has
    Activity ||--o{ ActivityLap : has
    Activity ||--o{ ActivityMetric : has
    Activity o|--o{ SourceFile : provenance
    Route o|--o| SourceFile : provenance
    Activity o|--o{ Route : source
    Activity o|--o{ Segment : source
    Segment ||--o{ SegmentEffort : calculates
    Activity ||--o{ SegmentEffort : supplies
~~~

`ApplicationSetting` persists the single global map mode. `FileOperationJournal` records prepared, database-committed, completed, rolled-back, and failed copy/quarantine operations with root-relative paths. Owner-scoped mutation locks serialize imports, transfers, and deletion for affected profiles.

`SegmentLink` stores unique parent/child segment pairs with foreign keys that cascade relationship rows only. Relationship mutations use the same owner lock; attachment validates profile, sport, directional containment, and cycle avoidance. Section placements are derived from the ordered geometry matcher. Detail queries batch related summaries and select child efforts wholly inside the selected parent effort's activity-stream interval, without storing duplicate tracks or effort relationships.

Segment-path uploads bypass the activity queue. `SegmentPathReader` accepts one GPX, FIT segment/course, TCX, KML, or GeoJSON line, exposes only geometry and normalized format, and rejects FIT activities or multiple independent paths. The endpoint applies the requested inclusive trim and optional reversal before calling `ISegmentService`. Only the local WKB path and minimal source kind/name/format provenance are stored; staged and original path files are not retained.

The initializer creates the current schema when the database does not yet exist. Additive compatibility steps idempotently add segment provenance, effort-metric columns, the segment-link, organization, goal and exploration tables, and the activity mutation/input-version columns to supported earlier schemas. Existing values remain intact; startup neither calculates exploration memberships nor groups segments or recalculates their efforts. Startup then reports untracked originals, marks abandoned running imports interrupted, and recovers lifecycle journal state. Other older development schemas still require a fresh data root and reimport. A complete data-root backup still requires stopping the app.

## Activity transfer and deletion

Activity reassignment acquires both owner locks and preflights natural fingerprint, Garmin/Strava IDs, provider/hash, and provider/external-ID collisions. A collision blocks; no automatic merge occurs. Originals copy and hash-verify first. The transaction creates a completed target transfer batch, moves activity-owned rows and source provenance, clears source links from routes/segments that remain with the old profile, and removes stale efforts. Journal commits then remove unreferenced old copies, and both owners' statistics and segments are recomputed.

After acquiring both locks, reassignment revalidates the source owner. Inside the ownership transaction it maps assignments to destination tags by normalized name, creating missing definitions. Source tag definitions, saved searches, and goals stay with their owner. Activity deletion cascades assignments; profile deletion removes its tags, saved searches, goals, and definition revisions. Profile export reads activities, tags, assignments, typed saved criteria, and authoritative goal definitions in one consistent transaction, retaining the existing fields and export schema version 1.

Activity deletion resolves either selected IDs or an exact filtered-ID snapshot before confirmation, using the displayed resolved date windows. Filter edits clear selection and confirmation and block deletion until successfully applied. Later imports cannot enter an existing confirmation. Deletion then acquires every affected owner lock and rejects missing or changed IDs atomically. Originals with no remaining provenance reference move to per-file quarantine before the database transaction. The transaction removes activity provenance and activities; cascades remove streams, laps, metrics, and efforts; saved routes and segments keep their definitions but lose source links; affected segment ranks are repaired; and current record snapshots are removed. After commit, quarantine cleanup and per-owner record recomputation run independently, and Activities refreshes rows and totals. Incomplete file cleanup remains journaled for startup recovery, while missing current record snapshots cause the statistics repair worker to retry.

Profile deletion blocks active/recoverable imports, journals a move of the owner directory into quarantine, commits database deletion, then removes quarantine. A post-commit cleanup failure is durable and retried at startup.

## Web and privacy boundary

The host allows only `localhost`, `127.0.0.1`, and `[::1]`. Upload URLs remain internal implementation endpoints but require a valid antiforgery header before body reads plus the custom same-origin header. Oversized streams return 413 and partial staging is removed. Route and segment-path endpoints default to 50 MiB, validate file extensions before parsing, and always clean their request staging.

Security middleware sends a Blazor/MapLibre-compatible CSP, `frame-ancestors 'none'`, `X-Frame-Options: DENY`, `nosniff`, no-referrer, and a restrictive permissions policy. OpenFreeMap origins enter CSP only after the global online setting is enabled. Blank mode is the default and makes no third-party map request.

There is deliberately no authentication or authorization. The process binds to `http://localhost:8342` by default and Docker publishes only `127.0.0.1:8342`. Remote or multi-user hosting is unsupported.
