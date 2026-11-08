# Database scale testing

Run this from the repository root with the pinned .NET SDK, PowerShell 7, and Python 3. No additional packages are required. The opt-in workload never reads the configured application database or imports personal files.

## Build and prepare

```powershell
dotnet build ActivityExplorer.slnx --configuration Release --no-restore -m:1 -p:BuildInParallel=false
./scripts/run-database-load.ps1 -Mode Prepare -Output artifacts/my-load-run
./scripts/run-database-load.ps1 -Mode Profile -Output artifacts/my-load-run -Blocks 1 -Diagnostic
```

Preparation creates deterministic synthetic databases through the production EF Core model. Existing fixture files, run directories and campaign plans are never overwritten. The campaign plan records both assembly hashes and the experiment settings before trials begin; diagnostic comparisons have a separate plan. Use a new output directory for a new campaign. Preparing 1000× fixtures takes several minutes and hundreds of megabytes. SQLite foreign keys remain enabled. Every mapped table must contain data, and additional checks validate relationship ownership. Adding a mapped table without adding its fixture fails preparation.

The baseline graph contains 100 activities, two profiles, one stream and source per activity, two laps, metrics, tags and exploration memberships per activity, routes, linked segments and efforts, import batches, goals and revisions, snapshots, saved searches, gear, settings, watched folders, file-operation journals, and MCP receipts. `samples.json` records every actual table count and database size.

| Dimension | What grows by 1×, 10×, 100×, 1000× |
| --- | --- |
| `history` | Activities and associated catalogs and relationships; two profiles and their bounded benchmark snapshots stay fixed. |
| `profiles` | Entire profile graphs, including profile count; each profile retains 50 activities. |

At 1000× there are 100,000 activities and 200,000 rows in each two-per-activity relationship. These dimensions keep per-activity fanout and 60-point stream length fixed. They test graph volume and profile count, not thousandfold larger individual streams, concurrent writer contention, or arbitrary Cartesian expansion of every child collection. Activity dates repeat a bounded 2,400-day cycle; text and metric filters retain 20% and 50% selectivity. A selected tag or import batch still matches its original 50 activities, and calendar selectivity varies with the scale's date coverage. Compare before/after within each identical fixture when interpreting gains. Exploration includes overlapping cells and synthetic distinct graph cells; the distinct cell is an intentional query fixture rather than reconstructed GPS evidence.

The 23 scenarios call real production services: owner/all-profile search, deep pagination, selective text and metric filters, tags, import membership, detail relationship counts, filter options, sport counts, import history, route and segment libraries, effort history, dashboard, calendar, persisted records, record attempts/history, period records with track decoding, exploration, and maps. Nonempty and complete-result assertions catch empty shortcuts. Serialized result fingerprints must agree between variants before a pair contributes to a speed comparison.

## Compare an unfixed and fixed build

Before editing production code, snapshot the baseline executable and its native runtimes:

```powershell
$snapshot = 'artifacts/my-baseline-bin'
New-Item -ItemType Directory -Path $snapshot
Get-ChildItem tests/ActivityExplorer.Tests/bin/Release/net10.0 -File | Copy-Item -Destination $snapshot
Copy-Item tests/ActivityExplorer.Tests/bin/Release/net10.0/runtimes -Destination $snapshot -Recurse
git rev-parse HEAD | Set-Content "$snapshot/revision.txt"
# Apply the intended change, then rebuild Release.
./scripts/run-database-load.ps1 -Mode Compare -Output artifacts/my-load-run `
  -BaselineDll "$snapshot/ActivityExplorer.Tests.dll" -Blocks 16
python scripts/analyze-database-load.py --input artifacts/my-load-run/runs --output artifacts/my-load-run/report --expected-blocks 16
```

Copy only executable files and `runtimes`; build output can also contain unrelated browser captures and test artifacts. Keep the baseline snapshot immutable. To compare this change again, use the retained baseline binary or build the parent revision in a separate checkout. Labeling two copies of the current build as different variants cannot reproduce the historical measurement.

`-Scales @(1,10)` and `-Dimensions @('history')` narrow a run. `-Scenarios @('imports-owner','records-attempts')` selects named scenarios. Scenario filtering must match between variants. `-FixtureRoot` reuses an existing prepared fixture directory without copying or changing its data. `-Warmups` defaults to 2 and `-Repetitions` to 1; both settings are recorded in the campaign plan. `-BudgetMs 5000` sets the end-to-end query budget; `-ProcessTimeoutSeconds 180` bounds each child process. SQLite cancellation is cooperative, so an individual command can return after the service budget; its actual elapsed time is retained. Preparation has its own 20-minute process limit. Microsoft Testing Platform requires local process/named-pipe permissions; a restricted runner may need permission to launch its test host.

## Measurement and interpretation

The initial profiled change replaced repeated filtered `OFFSET` scans in record attempts, history and period overview with one ordered eligible-ID read followed by primary-key batches of at most 16 activities. The follow-up below replaces whole-activity scans with a narrow scalar stream and period overview with one projected stream feeding at most four payload evaluations. Stream-dependent attempts still use the ID array and 16-activity batches. Deferred read snapshots, eligibility, cancellation and tie ordering are preserved. Import-history counts begin with batch source references, count distinct current targets, and retain owner checks for transferred activities and routes.

Each block launches fresh baseline/candidate test processes. An independent seeded coin flip assigns AB or BA order for each block. Both processes use the same persistent fixture. Each scenario gets two excluded warmups and one timed invocation; scenario order is seeded per block and identical between variants. Runs are sequential to avoid measuring the suite's own contention. Stop unrelated intensive work before making capacity decisions.

Raw samples contain wall-clock service latency, cumulative managed allocations, EF reader-command count, status, budget, and result fingerprint. Assertions, result serialization, report writes, fixture creation and validation are outside the timed interval. Allocations include process background activity and are not retained or peak memory. This is warm-cache service profiling on one machine, not HTTP throughput, cold-disk performance, a CPU stack profile, or a production capacity prediction. Machine/runtime details accompany the samples.

Diagnostic runs use `diagnostic-baseline`/`diagnostic-candidate` labels. Their bound SQL parameters and `EXPLAIN QUERY PLAN` output are retained separately and excluded from statistical comparisons. Instrumentation can materially change latency. Capture candidate plans with `-Mode Profile -Diagnostic -Blocks 1 -ProfileVariant candidate`; the default DLL is the current Release build. Capture baseline plans by passing the baseline DLL to `-CandidateDll` with `-ProfileVariant baseline`.

The analyzer pairs process-block medians, requires at least 12 complete blocks, bootstraps the median paired log-speed ratio with 10,000 fixed-seed resamples, and computes exact two-sided paired sign tests. A directional latency conclusion requires adjusted p < 0.05 and a 95% interval excluding 1. Intervals are pointwise, not simultaneous family intervals. More blocks do not cure systematic machine or fixture bias.

Failure analysis compares each block's pass/fail outcome using exact conditional McNemar tests on discordant pairs. It requires complete paired observations and equal recorded budgets, and rejects missing arms, duplicate process files, and differing successful fingerprints. This endpoint was added during the initial campaign, prompted by exploratory diagnostic timeouts, and defaults to a post hoc label. Use `--failure-endpoint prespecified` only when it was declared before the comparison, as in the record follow-up. It estimates reliability against the chosen service budget on this machine, not a speed ratio for censored calls. Joint Holm adjustment includes both latency and failure hypotheses for every observed workload cell, including compromised cells: up to 368 hypotheses in the full 184-cell campaign. Default 16 blocks gives sufficient minimum p-value resolution; noisy effects can still remain inconclusive. Candidate maxima include all elapsed samples, including failed calls; budget margin is the common budget minus that maximum.

Timeouts, exceeded budgets, exceptions, missing pairs, and differing results are reported explicitly. Censored failures never become ordinary successful latency observations or inferred speedups. An incomplete cell receives no significance claim. The runner returns nonzero on setup/process failure; successfully recording a slow query or query exception still produces a successful collection process. Inspect failed-sample counts and exclusions in the report when using this as a gate.

Outputs include a standalone HTML report, latency-by-scale and paired-speed-ratio SVG charts, summary CSV, excluded-block CSV, provenance, and query plans. All raw data and disposable databases stay under the chosen ignored `artifacts/` directory. Nothing is uploaded. Retain the report and binary snapshots for reproducibility; remove only your campaign directory when finished. Ordinary tests skip the load workload unless `AE_DATABASE_LOAD_CONFIG` is explicitly set.

Run `python -B scripts/test_analyze_database_load.py` for the mathematical and evidence-handling regression checks. C# performance regressions run with the normal test suite. The initial harness effectiveness check removed one measured owner's exploration-index header in a disposable fixture, observed the completeness assertion report an error, restored the database, and observed the same scenario pass.

## Initial measured results — 6 November 2026

The initial campaign completed all 256 timed processes and 5,888 calls: 23 scenarios, two dimensions, four scales, 16 independent process pairs, and a 2,000 ms service budget. The runtime was .NET 10.0.14 with SQLite 3.53.4 on Windows build 26300 and 16 logical processors. There were no query exceptions or differences between available paired result fingerprints. Primary latency analysis found five improvements after joint Holm correction across 368 hypotheses, with no significant regressions detected.

| Fixed-owner history workload | Scale | Before median ms | After median ms | Paired speedup, pointwise 95% interval | Adjusted p |
| --- | --- | ---: | ---: | --- | ---: |
| Import history, all profiles | 10× | 38.39 | 4.71 | 8.14× [7.52, 8.59] | 0.01123 |
| Import history, one profile | 10× | 37.88 | 5.36 | 7.58× [6.55, 8.76] | 0.01123 |
| Import history, all profiles | 100× | 608.64 | 67.18 | 10.32× [8.09, 12.68] | 0.01123 |
| Import history, one profile | 100× | 599.34 | 61.11 | 9.87× [8.35, 12.17] | 0.01123 |
| Record attempts | 10× | 29.44 | 16.07 | 1.73× [1.66, 1.93] | 0.01123 |

Speedups summarize paired log ratios and can differ from the ratio of the two displayed medians. Censored cells receive no latency-ratio claim. At 100×, attempts and record history each changed from 16/16 budget failures to 0/16. At 1000×, both import-history scenarios changed from 16/16 failures to 0/16. These failure comparisons are supplementary and post hoc, as described above.

The remaining candidate budget breaches were:

| Workload | History scale | Before failures | After failures |
| --- | --- | ---: | ---: |
| Period records with stream decoding | 100× | 16/16 | 2/16 |
| Record attempts | 1000× | 16/16 | 4/16 |
| Record history | 1000× | 16/16 | 4/16 |
| Period records with stream decoding | 1000× | 16/16 | 16/16 |

Overall budget failures fell from 129 to 26. Every sampled candidate call in the profile-count dimension met the budget. These results apply to the documented fixture and machine; they do not establish production capacity or concurrent-load performance. Full samples, exclusions, plans, assembly hashes and visual reports are retained locally under `artifacts/database-load-results/`.

GPT-6.1 Sol reviewed query plans and implemented the targeted query changes; GPT-6.1 Terra reviewed schema relationships and the final patch; GPT-6 Astra reviewed methodology and independently reconciled all 184 reported cells against the raw samples.

## Record-budget follow-up — 6 November 2026

This comparison uses the first campaign's candidate as its baseline, rather than the original unfixed build. Its Infrastructure assembly SHA-256 is `E1BEA0D6AAD3EF1CCF9BA5F3B7CDCA7331C09BCB66910248D59128355D71323D`; the final candidate is `A2148DA95C64CA8694BD93E975E528B03F85A681BDA442EDE15C30AB7B4E0630`. Both executable snapshots and the original fixtures remain unchanged during collection.

An intermediate four-worker build (`AB74EDE182EF48767C7C40F9800EE9A99B8C4CC47F5DB1CA2825A03F2B8F2D5C`) failed the strict gate: one of 84 recorded candidate calls timed out at 2,003.2929 ms in the 1000× history period query. That campaign was stopped and retained under `artifacts/record-budget-results/`, including its aborted-process evidence. Increasing workers or changing the in-memory parser showed little diagnostic improvement. Its observations are not pooled into the final comparison, which uses a fresh frozen build and new AB/BA assignment seed.

The follow-up targets `records-attempts`, `records-history`, and `records-period-streams` at 100× and 1000× in both dimensions. The protocol declares 16 independent AB/BA process pairs, two excluded warmups, one timed call, a 2,000 ms budget, and joint Holm adjustment across 12 latency and 12 prespecified failure hypotheses. The observed acceptance rule requires all 192 candidate calls to succeed strictly below 2,000 ms. The protocol is retained in `artifacts/record-budget-final-results/protocol.json` before collection.

Whole-activity attempts and history now stream only their needed scalar fields, retaining the existing C# eligibility and tie comparisons. Period records use one projected query, reuse owner names already resolved in the same snapshot, prepare each effort stream once per category, skip unreachable targets, and materialize only winning windows. Forward interpolation cursors retain the original formulas; extreme timestamp spans that collapse distinct ticks into equal relative doubles use the existing binary-search path. The Brotli JSON format is unchanged; a `TrackPoint` object factory avoids positional-constructor buffering. Up to four four-row batches are evaluated concurrently per request, while the EF reader remains serialized. A batch merges its candidates under one lock using the canonical comparison; the final partial batch is retained and cancellation propagates into the reader. At most 16 logical payload rows are in flight. No payload or query-result cache was added.

Complete high-budget correctness replays at both history scales matched all six baseline fingerprints exactly. These replays are separate from the timed comparison and contribute no speed claims. Regression checks cover nonfinite summary eligibility, bounded query count, all record categories and sports, deterministic ties, cancellation with immediate retry, legacy JSON, and exact winning-window parity against the retained multiple-attempt binary-search implementation. The query-count guard fails against the previous build (seven reads where at most two are allowed) and passes against the new build. Sol, Terra, and Astra reviewed the final query, codec, numerical, concurrency, and statistical changes.

To reproduce the follow-up using the retained local snapshots:

```powershell
./scripts/run-database-load.ps1 -Mode Compare -Output artifacts/my-record-follow-up `
  -FixtureRoot artifacts/database-load-results/fixtures `
  -BaselineDll artifacts/database-load-candidate-bin/ActivityExplorer.Tests.dll `
  -CandidateDll artifacts/record-budget-final-bin/ActivityExplorer.Tests.dll `
  -Scales @(100,1000) -Dimensions @('history','profiles') `
  -Scenarios @('records-attempts','records-history','records-period-streams') `
  -Blocks 16 -BudgetMs 2000 -Warmups 2 -Repetitions 1 -Seed 5372027
python scripts/analyze-database-load.py --input artifacts/my-record-follow-up/runs `
  --output artifacts/my-record-follow-up/report --expected-blocks 16 --failure-endpoint prespecified
```

The final campaign completed 128 timed processes and 384 calls. **All 192 candidate calls succeeded below 2,000 ms**, with no query exceptions or result differences. The slowest candidate took 1,609.1803 ms, leaving 390.8197 ms of observed margin. The baseline had 47 timeouts and one completed over-budget call. The 48 censored baseline blocks are excluded from latency inference and retained in failure comparisons.

| History workload | 100× median ms | 100× maximum ms | 1000× median ms | 1000× maximum ms |
| --- | ---: | ---: | ---: | ---: |
| Record attempts | 25.45 | 30.62 | 234.72 | 313.29 |
| Record history | 20.19 | 44.80 | 300.64 | 586.84 |
| Period records with streams | 257.40 | 477.54 | 1,257.47 | 1,609.18 |

Every profile-count candidate call also met the target; its largest observed time was 15.7242 ms. There were eight significant latency improvements after joint Holm correction across all 24 hypotheses (adjusted p = 0.000732421875), and no significant regressions. At 100× history, attempts improved 9.91× [8.30, 11.92] and history 10.72× [7.38, 13.34], with pointwise 95% intervals. The other six significant improvements were in the profile-count dimension. Censored cells receive no speedup claim.

At 1000× history, failures fell from 15/16 to 0/16 for attempts and history (adjusted p = 0.00091552734375 each), and 16/16 to 0/16 for period records (adjusted p = 0.000732421875). At 100× history, the period query changed from 2/16 failures to 0/16; that failure difference was inconclusive (adjusted p = 1). The strict observed budget gate passed independently of these hypothesis tests. Zero failures in 16 trials per cell does not establish future reliability: its one-sided 95% failure-probability upper bound is about 17.1%. These are warm single-client results on the documented machine; cold calls and concurrent requests are outside this claim.

Separate complete 1000× diagnostic replays captured the SQL and plans. All three record operations now use two EF reader commands, compared with 1,565 before. The period query uses the existing owner/sport/start-time index and stream primary-key lookup, without the owner join or a temporary sorting tree. Diagnostic timings are excluded from inference. Astra independently reconciled every cell, all 24 exact and adjusted tests, bootstrap intervals, maximum/margin, reference fingerprints, assembly hashes, and the 64 randomized assignments (31 AB, 33 BA), with no discrepancies.

The updated report, CSV, raw runs, SQL plans, figures, protocol, and strict acceptance audit are retained in `artifacts/record-budget-final-results/`. The first full campaign remains in `artifacts/database-load-results/`; the failed intermediate follow-up remains in `artifacts/record-budget-results/`. The interactive comparison uses only the final follow-up's 12 cells, with the first candidate explicitly labeled as its baseline.

Final validation passed: Release build with zero warnings, formatting verification, all 772 C# tests, all seven analyzer regression checks, and the PowerShell parser. The complete test report is `artifacts/record-budget-final-validation/tests.xml`. Chart checks validated its default payload and all 18 control configurations; control/state wiring was checked separately, and the standalone PNG was visually inspected. No temporary production probes remain.

## Cold four-client workload

`scripts/run-database-pressure.ps1` measures all 23 service queries in the original suite and one mixed burst of period records, record attempts, record history, and import history. The default comparison covers 100× and 1000× growing history and growing profile counts. It uses the previous warm-budget candidate as the baseline. Both arms use the same measurement driver and .NET Server GC. The baseline uses the previous shared SQLite cache, and the candidate uses private SQLite caches, matching their respective application settings. The plan records binary hashes, fixture manifests, and the seed for the AB/BA order.

Each cold burst opens a fresh unpooled SQLite connection per client on a unique fixture copy created with [CopyFileExW and COPY_FILE_NO_BUFFERING](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-copyfileexw). The default schedules four workload cases per process. Each process stages its cold copies with up to two copy workers and waits five seconds before collection. The four clients start from a common arrival timestamp. Elapsed time includes queueing, opening the connection, executing the service query, and materializing its result. Two four-client warmups on each of two separate remapped fixtures exercise runtime paths before each measured burst. Both arms use private caches for the primers, and a connection initializes the WAL state on each primer before its warmups. The harness hashes and removes the timed copies after every measured burst in that process has drained. Device and background file caches are uncontrolled. The suite measures first access to a fresh database file in a warmed runtime.

With `-QuiescentHost`, both variants wait after priming and immediately before the common arrival. Three consecutive 500 ms samples must show current disk queue at most 1, average disk queue at most 1.5, and total CPU at most 60%. The wait is capped at 300 seconds. An invalid interval-counter sample is recorded and resets the consecutive window. Every observation and wait is retained separately from service latency. A setup failure stops collection, and every request outcome after arrival enters the comparison. This protocol measures cold queries from the recorded pre-arrival host state. [Windows performance counters](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcountervalue)

Prepare the baseline metadata against the original synthetic fixtures. Copy each synthetic database and its matching `.manifest` into a fresh directory under `artifacts/database-pressure-index-candidates` before preparing candidate metadata. Use `-UpgradeReadIndexes -PrepareRecordEffortSummaries -PrepareExplorationCellSets` in candidate Prepare mode to run the production initializer's read migrations on those copies. The original 25 tables remain the comparison inputs. The fixture manifest records the four derived tables separately. For runtime primers, copy the 1× and 100× history fixtures into a fresh directory under `artifacts/database-pressure-runtime` and prepare them with `-PrimeClone` and the same three migration switches. Use those same two runtime manifests for both variants. `-ValidateManifests` checks the manifests against the comparison plan before running any timed process.

The strict budget gate requires all 6,144 candidate requests across 96 cells and 16 paired blocks to succeed in under 2,000 ms and match independent baseline result fingerprints. Six scenario batches produce 768 timed processes across both arms. The runner assigns an AB/BA order to each fixture and block and applies it to adjacent process pairs for each batch. A wave's latency is its slowest request, and a wave fails when any of its requests fails. The four requests contribute one observation per wave to inference. The analyzer reports paired sign tests for successful burst latency and exact McNemar tests for budget failures, with joint Holm adjustment across the 192 hypotheses. Timeouts remain in the failure comparison and prevent a complete latency ratio for their cell.

Activity paging selects keys before loading page details. Covering indexes supply scalar filters, totals, date and ownership checks, and route-library projections without reading geometry. Map counts walk a partial eligibility index and map output projects XY directly from WKB geometry. Import history and map reads use deferred snapshots to avoid reserving SQLite's writer lock. Aggregate exploration streams activity headers and compact cell sets from an owner-clustered projection through the native SQLite reader. The index writer publishes each set in the same transaction as its source memberships, and triggers refresh or invalidate the projection when those inputs change. The service checks coverage in the same snapshot and falls back to the prior compact or authoritative membership path as needed. Cell details still use indexed lookup. The driver counts EF readers, and the timed span includes the native exploration reader.

Aggregate exploration, the default activity map, and dated period records scoped to an owner and sport can map up to 1 GiB of the main database file per connection. The services restore the previous limit after the read transaction ends, including after cancellation. The limit is an address-space ceiling, and the operating system pages in the data on demand. The benchmark checks SQLite's effective limit at the first EF reader for each request. [SQLite memory-mapped I/O](https://sqlite.org/mmap.html)

Period records read versioned per-stream effort summaries with 34 bytes per winning target. The importer writes the derived values with the stream, and startup fills missing rows in committed batches. Updates to a stream or its sport invalidate the affected summary. Dated requests over at least 1,024 eligible activities in a single owner and sport read the activity fields and summaries from a projection keyed by owner, sport, time, and activity. The query checks coverage in the same snapshot and falls back to the canonical reader if the projection or a summary is unavailable. It reuses a per-request decode buffer and compares value candidates without allocating an attempt for every value.

The four derived tables each contain 100,000 rows at 1000×. Together with the added indexes, they increase the 1000× synthetic fixture sizes by about 19.5%. The original 25 tables were checked for exact value and row ID equality across all six prepared fixture pairs.

Exploratory reader timing separated several causes. In one fixed build with the same 1000× history fixture, exploration took 5.13 seconds with SQLite shared cache and 2.22–2.58 seconds with private caches. The reader traces showed 1.32–1.57 seconds in membership iteration even after that change. The dense membership scan and compact effort summaries were tested separately from the final statistical campaign. SQLite's guidance also recommends private caches for databases using WAL. [SQLite shared-cache guidance](https://sqlite.org/sharedcache.html) · [Microsoft.Data.Sqlite connection settings](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)

The initial candidate passed a complete 384-request cold screen, with exact independent result matches. Its slowest request took 1,799.0433 ms in 1000× history exploration. The first paired attempt stopped after a shared-cache SQLite open failed on a separate baseline primer. A second attempt with common private-cache primers passed its first two candidate blocks, then stopped when 1000× history exploration reached 2,103.0771 ms. The compact cell sets address the membership reads in that case. A later mixed screen stalled in the opening reads of all four clients and used only 141 ms of process CPU in a 2,829 ms burst. The staged protocol separates copying and verification from collection. A subsequent paired run still found an exploration burst that took 2.06 seconds. In uncensored exploration diagnostics on that build, three private-cache trials without mapping took 2.12–4.28 seconds and three with mapping took 0.61–1.43 seconds. A focused replay with scoped mapping still timed out at 2.03 seconds. The owner-clustered projection removes the per-activity lookups from that read.

The next screen exposed a 2.02-second period-record burst. In five uncensored controls on that build, period-record waves with default mapping took 1.20–5.54 seconds. Five waves with a 1 GiB mapping limit took 0.83–1.16 seconds. The slowest default trial spent 3.32 seconds in the summary reader for its slowest client. The clustered period projection removes the separate row-address and summary lookup passes.

A later screen recorded three successive misses in exploration, the map, and the mixed burst during a low-progress interval. In five uncensored map controls with default mapping, bursts took 0.73–11.85 seconds. The five interleaved controls with a 1 GiB limit took 0.20–0.46 seconds, with all 40 results matching the independent map reference. Aggregate Windows counters showed periods of heavy disk or CPU activity around some control arrivals. The next ungated screen still showed simultaneous stalls with mapping and the clustered reads enabled. Exploratory runs and aborted comparisons are excluded from inference.

To repeat the paired campaign with the retained local fixtures and binaries:

```powershell
$primers = @(
  'artifacts/database-pressure-runtime-metadata-v8/history-1.json',
  'artifacts/database-pressure-runtime-metadata-v8/history-100.json'
)
./scripts/run-database-pressure.ps1 -Mode Compare -Output artifacts/my-cold-comparison `
  -BaselineDll artifacts/database-pressure-baseline-v14-bin/ActivityExplorer.Tests.dll `
  -CandidateDll artifacts/database-pressure-candidate-v14-bin/ActivityExplorer.Tests.dll `
  -FixtureRoot artifacts/database-load-results/fixtures -MetadataRoot artifacts/database-pressure-metadata-v2 `
  -CandidateFixtureRoot artifacts/database-pressure-index-candidates/v8 `
  -CandidateMetadataRoot artifacts/database-pressure-candidate-metadata-v8 -CandidateDerivedSummaryVersion 702 `
  -CandidateExplorationCellSetVersion 1 -CandidateExplorationAggregateVersion 1 -CandidatePeriodRecordRowVersion 1 `
  -RuntimeMetadataFiles $primers -Dimensions @('history','profiles') -Scales @(1000,100) `
  -Blocks 16 -BudgetMs 2000 -Seed 5372028 -GcMode Server -SqliteCacheMode Shared `
  -CandidateSqliteCacheMode Private -ScenariosPerProcess 4 `
  -CandidateMmapLimitsByRole @{exploration = 1073741824L; 'records-period-streams' = 1073741824L; 'map-activities' = 1073741824L} `
  -QuiescentHost -ValidateManifests -StopOnCandidateFailure -ProcessTimeoutSeconds 1800
python -B scripts/analyze-database-load.py --input artifacts/my-cold-comparison/runs `
  --output artifacts/my-cold-comparison/report --expected-blocks 16 --failure-endpoint prespecified `
  --require-candidate-budget --goldens artifacts/database-pressure-goldens-v1.json
python -B scripts/audit-pressure-results.py --runs artifacts/my-cold-comparison/runs `
  --report artifacts/my-cold-comparison/report --goldens artifacts/database-pressure-goldens-v1.json `
  --output artifacts/my-cold-comparison/report/independent-audit.json
```
