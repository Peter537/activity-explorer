"""Run with: python -B scripts/test_analyze_database_load.py"""

import importlib.util
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("database_load", Path(__file__).with_name("analyze-database-load.py"))
analysis = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analysis)
analysis.BOOTSTRAPS = 100  # Constant-ratio fixtures have identical percentiles at any resample count.
audit_spec = importlib.util.spec_from_file_location("pressure_audit", Path(__file__).with_name("audit-pressure-results.py"))
audit = importlib.util.module_from_spec(audit_spec)
audit_spec.loader.exec_module(audit)


def sample(variant, block, elapsed):
    return dict(variant=variant, block=block, elapsedMs=elapsed, budgetMs=5000, scale=10, dimension="history",
                scenario="imports", status="ok", resultFingerprint="same-result", source=f"{variant}-{block}.json")


def paired_blocks(candidate_ms=5):
    return {i: {"baseline": [sample("baseline", i, 10)], "candidate": [sample("candidate", i, candidate_ms)]}
            for i in range(16)}


def summarize(blocks, name="imports"):
    exclusions = []
    row = analysis.summarize_cell(("history", name, 10), blocks, exclusions, expected_blocks=16)
    return row, exclusions


def wave(variant, block=0, base="imports-all", failed_slot=None):
    roles = analysis.MIXED_REQUESTS if base == "mixed" else (base,) * 4
    requests = []
    for slot, role in enumerate(roles):
        failed = slot == failed_slot
        requests.append(dict(slot=slot, scenario=role, elapsedMs=5100 if failed else 10 + slot, enteredMs=0.1,
                             status="timeout" if failed else "ok", resultFingerprint=None if failed else role + "-hash"))
    fingerprint = hashlib.sha256("\n".join(f"{r['slot']}|{r['scenario']}|{r['resultFingerprint']}" for r in requests).encode()).hexdigest().upper()
    return dict(sample(variant, block, max(r["elapsedMs"] for r in requests)), baseScenario=base,
                scenario=f"{base}.warm-burst", regime="warm-burst", cacheMode="warm-sqlite-os-uncontrolled",
                concurrency=4, requests=requests, status="ok" if failed_slot is None else "timeout",
                resultFingerprint=fingerprint if failed_slot is None else None)


class DatabaseLoadAnalysisTests(unittest.TestCase):
    def test_transient_host_counter_errors_reset_window_and_require_all_null_metrics(self):
        policy = dict(pollMs=500, consecutiveSamples=3, maxCurrentDiskQueue=1,
                      maxAverageDiskQueue=1.5, maxCpuPercent=60, maxWaitMs=300000)
        trial = dict(variant="candidate", baseScenario="exploration", regime="cold-burst",
                     arrivalUtc="2026-11-07T12:00:03.050Z")
        observations = [dict(utc=f"2026-11-07T12:00:0{second}Z", currentDiskQueue=0,
                             averageDiskQueue=0, cpuPercent=5, errorCode=None)
                        for second in ("0.500", "1.000", "1.500", "2.000", "2.500", "3.000")]
        observations[2].update(currentDiskQueue=None, averageDiskQueue=None, cpuPercent=None, errorCode="0x800007D6")
        window = dict(scenario="exploration", regime="cold-burst", startedUtc="2026-11-07T12:00:00Z",
                      completedUtc="2026-11-07T12:00:03.010Z", passed=True, observations=observations)
        metadata = dict(config=dict(hostGate=policy), hostReadiness=[window])
        plan = dict(HostGate=policy)
        for code in ("0x800007D4", "0x800007D5", "0x800007D6", "0x800007D7", "0x800007D8", "0xC0000BBA"):
            observations[2]["errorCode"] = code
            self.assertEqual(analysis.host_readiness_issues([trial], metadata, plan), [])
            self.assertEqual(len(audit.verify_host_readiness([trial], metadata, plan)), 1)
        for changes in (dict(errorCode="0xDEADBEEF"), dict(cpuPercent=0), dict(errorCode=None)):
            altered = copy.deepcopy(metadata)
            altered["hostReadiness"][0]["observations"][2].update(changes)
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                audit.verify_host_readiness([trial], altered, plan)
            self.assertTrue(analysis.host_readiness_issues([trial], altered, plan))
        missing = copy.deepcopy(metadata)
        del missing["hostReadiness"][0]["observations"][2]["cpuPercent"]
        with self.assertRaises(ValueError):
            audit.verify_host_readiness([trial], missing, plan)
        short = copy.deepcopy(metadata)
        short["hostReadiness"][0]["observations"].pop()
        short["hostReadiness"][0]["completedUtc"] = "2026-11-07T12:00:02.510Z"
        with self.assertRaisesRegex(ValueError, "fresh passing window"):
            audit.verify_host_readiness([dict(trial, arrivalUtc="2026-11-07T12:00:02.550Z")], short, plan)
        self.assertTrue(analysis.host_readiness_issues([trial], short, plan))

    def test_host_gate_uses_first_valid_window_after_priming_without_outcome_selection(self):
        policy = dict(pollMs=500, consecutiveSamples=3, maxCurrentDiskQueue=1,
                      maxAverageDiskQueue=1.5, maxCpuPercent=60, maxWaitMs=120000)
        trial = dict(variant="candidate", baseScenario="exploration", regime="cold-burst",
                     arrivalUtc="2026-11-07T12:00:03.050Z", status="timeout")
        readings = [dict(utc=f"2026-11-07T12:00:0{second}Z", currentDiskQueue=0,
                         averageDiskQueue=2 if index == 0 else 0, cpuPercent=5)
                    for index, second in enumerate(("1.500", "2.000", "2.500", "3.000"))]
        window = dict(scenario="exploration", regime="cold-burst", startedUtc="2026-11-07T12:00:01Z",
                      completedUtc="2026-11-07T12:00:03.010Z", passed=True, observations=readings)
        metadata = dict(config=dict(hostGate=policy), hostReadiness=[window],
                        priming=[dict(scenario="exploration", regime="cold-burst", completedUtc="2026-11-07T12:00:01Z")])
        plan = dict(HostGate=policy)
        waits = audit.verify_host_readiness([trial], metadata, plan)
        self.assertEqual([v for v, _ in waits], ["candidate"])
        self.assertAlmostEqual(waits[0][1], 2010.0)
        self.assertEqual(analysis.host_readiness_issues([trial], metadata, plan), [])
        mutations = [lambda m: m["hostReadiness"].append(copy.deepcopy(window)),
                     lambda m: m["hostReadiness"][0].update(passed=False),
                     lambda m: m["hostReadiness"][0].update(completedUtc="2026-11-07T12:00:04Z"),
                     lambda m: m["priming"][0].update(completedUtc="2026-11-07T12:00:01.100Z"),
                     lambda m: m["hostReadiness"][0]["observations"][0].update(averageDiskQueue=0),
                     lambda m: m["hostReadiness"][0]["observations"][1].update(utc="2026-11-07T12:00:01.600Z"),
                     lambda m: m["hostReadiness"][0]["observations"][-1].update(cpuPercent=61),
                     lambda m: m["hostReadiness"][0]["observations"][0].update(currentDiskQueue=-1),
                     lambda m: m["hostReadiness"][0]["observations"][0].update(cpuPercent=float("nan")),
                     lambda m: m["hostReadiness"][0]["observations"][0].update(averageDiskQueue=float("inf")),
                     lambda m: m["config"].update(hostGate=None)]
        for index, mutate in enumerate(mutations):
            altered = copy.deepcopy(metadata)
            mutate(altered)
            with self.subTest(mutation=index):
                with self.assertRaises(ValueError):
                    audit.verify_host_readiness([trial], altered, plan)
                self.assertTrue(analysis.host_readiness_issues([trial], altered, plan))
        with self.assertRaisesRegex(ValueError, "fresh"):
            audit.verify_host_readiness([dict(trial, arrivalUtc="2026-11-07T12:00:05Z")], metadata, plan)
        self.assertEqual(audit.verify_host_readiness([trial], {}, {}), [])
        with self.assertRaises(ValueError):
            audit.verify_host_readiness([trial], dict(hostReadiness=[window]), {})

    def test_cold_staging_precedes_all_collection_and_integrity_follows_last_wave(self):
        plan = dict(Scenarios=["imports-all", "mixed"], Regimes=["cold-solo", "cold-burst", "warm-burst"],
                    ColdCopyWorkers=2, ColdCopySettleMs=5000)
        cases = [(s, r) for s in plan["Scenarios"] for r in plan["Regimes"] if (s, r) != ("mixed", "cold-solo")]
        samples = [dict(baseScenario=s, regime=r, elapsedMs=100, arrivalUtc=f"2026-11-07T12:00:{10 * (i + 1):02}Z")
                   for i, (s, r) in enumerate(cases)]
        metadata = dict(fixture=dict(databaseBytes=100), coldPreparation=dict(replicaCount=3, copyConcurrency=2,
            requiredBytes=300, freeBytes=1000, settleMs=5000, copiesReadyUtc="2026-11-07T12:00:00Z",
            collectionReadyUtc="2026-11-07T12:00:05Z"),
            copyChecks=[dict(scenario=s, regime=r, verifiedUtc="2026-11-07T12:00:51Z") for s, r in cases])
        self.assertEqual(analysis.cold_stage_issues(samples, metadata, plan), [])
        audit.verify_cold_stage(samples, metadata, plan)
        mutations = [lambda m: m["coldPreparation"].update(replicaCount=4),
                     lambda m: m["coldPreparation"].update(copyConcurrency=1),
                     lambda m: m["coldPreparation"].update(requiredBytes=301),
                     lambda m: m["coldPreparation"].update(freeBytes=299),
                     lambda m: m["coldPreparation"].update(settleMs=4999),
                     lambda m: m["coldPreparation"].update(collectionReadyUtc="2026-11-07T12:00:04.900Z"),
                     lambda m: m["coldPreparation"].update(collectionReadyUtc="2026-11-07T12:00:11Z"),
                     lambda m: m["copyChecks"][0].update(verifiedUtc="2026-11-07T12:00:50Z"),
                     lambda m: m["copyChecks"].pop(),
                     lambda m: m["copyChecks"][0].pop("verifiedUtc")]
        for i, mutate in enumerate(mutations):
            altered = copy.deepcopy(metadata)
            mutate(altered)
            with self.subTest(mutation=i):
                self.assertTrue(analysis.cold_stage_issues(samples, altered, plan))
                with self.assertRaises((ValueError, KeyError)):
                    audit.verify_cold_stage(samples, altered, plan)
        self.assertTrue(analysis.cold_stage_issues(samples[:-1], metadata, plan))
        self.assertEqual(analysis.cold_stage_issues(samples, {}, {}), [])
        batched = dict(plan, ScenarioBatches=[["imports-all"], ["mixed"]])
        subset = [s for s in samples if s["baseScenario"] == "imports-all"]
        prepared = copy.deepcopy(metadata)
        prepared["config"] = dict(scenarios=["imports-all"])
        prepared["copyChecks"] = [c for c in prepared["copyChecks"] if c["scenario"] == "imports-all"]
        prepared["coldPreparation"].update(replicaCount=2, requiredBytes=200)
        self.assertEqual(analysis.cold_stage_issues(subset, prepared, batched), [])
        audit.verify_cold_stage(subset, prepared, batched)
        invalid_plans = [dict(batched, ScenarioBatches=[["mixed"], ["imports-all"]]),
                         dict(batched, ScenarioBatches=[["imports-all"], ["imports-all", "mixed"]]),
                         dict(batched, ScenarioBatches=[["imports-all"], []])]
        for altered in invalid_plans:
            self.assertTrue(analysis.cold_stage_issues(subset, prepared, altered))
            with self.assertRaises(ValueError):
                audit.verify_cold_stage(subset, prepared, altered)
        prepared["config"]["scenarios"] = ["imports-all", "mixed"]
        self.assertTrue(analysis.cold_stage_issues(subset, prepared, batched))
        with self.assertRaises(ValueError):
            audit.verify_cold_stage(subset, prepared, batched)

    def test_derived_summary_manifest_requires_declared_complete_valid_source_rows(self):
        original = dict(counts={"Activities": 100, "ActivityStreams": 80})
        summary = dict(rowCount=80, sourceStreamCount=80, invalidRows=0, computationVersion=701)
        candidate = dict(original, derivedTables=dict(activityRecordEffortSummaries=summary))
        plan = dict(BaselineDerivedSummaryVersion=None, CandidateDerivedSummaryVersion=701)
        self.assertEqual(analysis.derived_summary_issues(original, "baseline", plan), [])
        self.assertEqual(analysis.derived_summary_issues(candidate, "candidate", plan), [])
        self.assertTrue(analysis.derived_summary_issues(candidate, "baseline", plan))
        self.assertTrue(analysis.derived_summary_issues(original, "candidate", plan))
        for field, value in (("rowCount", 79), ("sourceStreamCount", 79), ("invalidRows", 1), ("computationVersion", 700)):
            altered = dict(candidate, derivedTables=dict(activityRecordEffortSummaries=dict(summary, **{field: value})))
            with self.subTest(field=field):
                self.assertTrue(analysis.derived_summary_issues(altered, "candidate", plan))
        self.assertEqual(original["counts"], candidate["counts"])
        original["counts"]["ActivityExplorationIndexes"] = 60
        cell_set = dict(rowCount=60, sourceIndexCount=60, invalidRows=0, computationVersion=1)
        both = dict(candidate, derivedTables=dict(candidate["derivedTables"], activityExplorationCellSets=cell_set))
        expanded = dict(plan, CandidateExplorationCellSetVersion=1, BaselineExplorationCellSetVersion=None)
        self.assertEqual(analysis.derived_summary_issues(both, "candidate", expanded), [])
        audit.verify_derived(both, "candidate", expanded)
        audit.verify_derived(original, "baseline", expanded)
        self.assertTrue(analysis.derived_summary_issues(candidate, "candidate", expanded))
        self.assertTrue(analysis.derived_summary_issues(both, "candidate", plan))
        for field, value in (("rowCount", 59), ("sourceIndexCount", 59), ("invalidRows", 1), ("computationVersion", 2)):
            altered = dict(both, derivedTables=dict(both["derivedTables"], activityExplorationCellSets=dict(cell_set, **{field: value})))
            with self.subTest(exploration_field=field):
                self.assertTrue(analysis.derived_summary_issues(altered, "candidate", expanded))
                with self.assertRaisesRegex(ValueError, "derived integrity"):
                    audit.verify_derived(altered, "candidate", expanded)

    def test_independent_audit_reconciles_raw_waves_and_rejects_summary_tampering(self):
        scratch = Path(__file__).resolve().parents[1] / "artifacts" / "database-load-analysis-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temp:
            root = Path(temp)
            runs, report = root / "runs", root / "report"
            runs.mkdir()
            plan = dict(Dimensions=["history"], Scales=[10], Scenarios=["imports-all"], Regimes=["cold-burst"],
                        ScenarioBatches=[["imports-all"]],
                        Blocks=16, BudgetMs=5000, GcMode="Server", SqliteCacheMode="Shared", Seed=5372028,
                        BaselineSqliteCacheMode="Shared", CandidateSqliteCacheMode="Private",
                        DriverHash="driver", BaselineAssemblyHash="baseline", CandidateAssemblyHash="candidate")
            plan["HostGate"] = dict(pollMs=500, consecutiveSamples=3, maxCurrentDiskQueue=1,
                                    maxAverageDiskQueue=1.5, maxCpuPercent=60, maxWaitMs=120000)
            (runs / "campaign-plan.json").write_text(json.dumps(plan), encoding="utf-8")
            for i in range(16):
                for variant in analysis.VARIANTS:
                    trial = wave(variant, i)
                    trial.update(scenario="imports-all.cold-burst", regime="cold-burst", cacheMode="unbuffered-file-first-touch")
                    mode = plan[variant.title() + "SqliteCacheMode"]
                    trial["sqliteCacheMode"] = mode
                    for request in trial["requests"]:
                        request["effectiveMmapLimitBytes"] = [0]
                    metadata = dict(driverHash="driver", productionHash=variant, serverGc=True, sqliteCacheMode=mode,
                                    config=dict({k: trial[k] for k in ("variant", "block", "dimension", "scale", "budgetMs")}, cacheMode=mode, scenarios=["imports-all"]))
                    trial["arrivalUtc"] = "2026-11-07T12:00:01.600Z"
                    metadata["config"]["hostGate"] = plan["HostGate"]
                    metadata["hostReadiness"] = [dict(scenario="imports-all", regime="cold-burst",
                        startedUtc="2026-11-07T12:00:00Z", completedUtc="2026-11-07T12:00:01.500Z", passed=True,
                        observations=[dict(utc=f"2026-11-07T12:00:0{second}Z", currentDiskQueue=0,
                                           averageDiskQueue=0, cpuPercent=5) for second in ("0.500", "1.000", "1.500")])]
                    folder = runs / f"{variant}-{i}"
                    folder.mkdir()
                    (folder / "samples.json").write_text(json.dumps(dict(metadata=metadata, samples=[trial])), encoding="utf-8")
            golden = root / "goldens.json"
            golden.write_text(json.dumps([dict(dimension="history", scale=10, scenario="imports-all", resultFingerprint="imports-all-hash")]), encoding="utf-8")
            groups, sources, diagnostics = analysis.read_trials(runs)
            exclusions = []
            rows = [analysis.summarize_cell(k, v, exclusions, 16) for k, v in groups.items()]
            family = analysis.classify(rows)
            analysis.write_reports(report, rows, exclusions, sources, diagnostics, family, "prespecified", None)
            self.assertEqual((rows[0]["baselineSqliteCacheMode"], rows[0]["candidateSqliteCacheMode"]), ("Shared", "Private"))
            self.assertIn("does not isolate their individual effects", (report / "report.md").read_text(encoding="utf-8"))
            command = [sys.executable, "-B", str(Path(__file__).with_name("audit-pressure-results.py")),
                       "--runs", str(runs), "--report", str(report), "--goldens", str(golden), "--output", str(root / "audit.json")]
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            evidence = json.loads((root / "audit.json").read_text(encoding="utf-8"))
            self.assertEqual((evidence["cells"], evidence["candidateRequests"], evidence["hypotheses"]), (1, 64, 2))
            self.assertEqual((evidence["timedProcesses"], evidence["expectedProcesses"]), (32, 32))
            for variant in analysis.VARIANTS:
                self.assertEqual(evidence["hostSetupWaits"][variant], dict(count=16, minMs=1500, medianMs=1500, maxMs=1500))
            raw_path = runs / "candidate-0" / "samples.json"
            original = raw_path.read_text(encoding="utf-8")
            for flag in ("configured", "nested"):
                altered = json.loads(original)
                if flag == "configured":
                    altered["metadata"]["config"]["diagnostic"] = True
                else:
                    altered["samples"][0]["requests"][0]["diagnostics"] = []
                raw_path.write_text(json.dumps(altered), encoding="utf-8")
                result = subprocess.run(command, capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("Diagnostic instrumentation", result.stderr)
            raw_path.write_text(original, encoding="utf-8")
            summary = report / "summary.csv"
            import csv
            with summary.open(newline="", encoding="utf-8") as file:
                table = list(csv.DictReader(file))
            table[0]["candidateRequests"] = "63"
            with summary.open("w", newline="", encoding="utf-8") as file:
                writer = csv.DictWriter(file, fieldnames=list(table[0]))
                writer.writeheader()
                writer.writerows(table)
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("CSV mismatch", result.stderr)

    def test_runtime_priming_requires_independent_fixtures_and_complete_ordered_events(self):
        scratch = Path(__file__).resolve().parents[1] / "artifacts" / "database-load-analysis-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temp:
            root = Path(temp)
            paths = [root / f"prime-{scale}.json" for scale in (1, 100)]
            for path, scale in zip(paths, (1, 100)):
                path.write_text(json.dumps(dict(scale=scale, dimension="history", primed=True,
                    sourcePath=str(root / f"prime-{scale}.db"), ownerId=f"prime-owner-{scale}",
                    sourceSha256=f"prime-sha-{scale}")), encoding="utf-8")
            plan = dict(RuntimeMetadataFiles=list(map(str, paths)), RuntimePrimeIterations=2,
                        RuntimePrimeConcurrency=4, WarmupBudgetMs=60000,
                        Scenarios=["imports-all"], Regimes=["warm-burst"])
            problems = []
            primers = analysis.planned_primers(plan, problems)
            self.assertEqual(problems, [])
            trial = dict(wave("candidate"), arrivalUtc="2026-11-07T12:00:10Z")
            metadata = dict(config=dict(runtimeMetadataFiles=list(map(str, paths)), fixtureDirectory=str(root / "target")),
                            fixture=dict(ownerId="target", sourceSha256="target"), priming=[])
            for index, (scale, iteration) in enumerate((s, i) for s in (1, 100) for i in range(2)):
                metadata["priming"].append(dict(scenario="imports-all", regime="warm-burst", scale=scale,
                    iteration=iteration, sourcePath=str(root / f"prime-{scale}.db"), concurrency=4, budgetMs=60000,
                    startedUtc=f"2026-11-07T12:00:0{index * 2}Z", completedUtc=f"2026-11-07T12:00:0{index * 2 + 1}Z"))
            self.assertEqual(analysis.priming_issues(trial, metadata, plan, primers), [])
            private_plan = dict(plan, RuntimePrimeSqliteCacheMode="Private")
            self.assertIn("SQLite cache mode", " ".join(analysis.priming_issues(trial, metadata, private_plan, primers)))
            for event in metadata["priming"]:
                event["sqliteCacheMode"] = "Private"
            self.assertEqual(analysis.priming_issues(trial, metadata, private_plan, primers), [])
            metadata["priming"][0]["sqliteCacheMode"] = "Shared"
            self.assertIn("SQLite cache mode", " ".join(analysis.priming_issues(trial, metadata, private_plan, primers)))
            metadata["priming"][0]["sqliteCacheMode"] = "Private"
            mutations = [lambda m: m["priming"].pop(),
                         lambda m: m["priming"].reverse(),
                         lambda m: m["priming"][0].update(iteration=1),
                         lambda m: m["priming"][0].update(concurrency=1),
                         lambda m: m["priming"][0].update(budgetMs=2000),
                         lambda m: m["priming"][-1].update(completedUtc="2026-11-07T12:00:11Z"),
                         lambda m: m["priming"][1].update(startedUtc="2026-11-07T12:00:00Z"),
                         lambda m: m["fixture"].update(ownerId="prime-owner-1"),
                         lambda m: m["fixture"].update(sourcePath=str(root / "prime-1.db")),
                         lambda m: m["fixture"].update(primed=True),
                         lambda m: m["config"]["runtimeMetadataFiles"].reverse(),
                         lambda m: m["priming"].append(dict(m["priming"][0], scenario="unplanned"))]
            for index, mutate in enumerate(mutations):
                altered = copy.deepcopy(metadata)
                mutate(altered)
                with self.subTest(mutation=index):
                    self.assertTrue(analysis.priming_issues(trial, altered, plan, primers))
            problems = []
            analysis.planned_primers(dict(plan, RuntimePrimeIterations=1), problems)
            self.assertTrue(problems)
            problems = []
            analysis.planned_primers(dict(plan, CandidateDerivedSummaryVersion=701), problems)
            self.assertTrue(any("derived summary" in issue for issue in problems))
            self.assertEqual(analysis.normalized_fixture(metadata["fixture"]),
                             analysis.normalized_fixture(dict(metadata["fixture"], sourcePath=None, primed=False)))

    def test_four_clients_are_one_inference_block_and_failures_are_counted_per_request(self):
        blocks = {i: {v: [wave(v, i, "mixed", 2 if v == "baseline" and i == 3 else None)]
                      for v in analysis.VARIANTS} for i in range(16)}
        for arms in blocks.values():
            for samples in arms.values():
                analysis.validate_wave(samples[0])
        row, _ = summarize(blocks, "mixed.warm-burst")
        self.assertEqual((row["baselineRequests"], row["baselineFailedRequests"], row["candidateRequests"]), (64, 1, 64))
        self.assertEqual((row["pairedBlocks"], row["failurePairedBlocks"], row["failureImprovedBlocks"]), (15, 16, 1))
        self.assertEqual((row["regime"], row["concurrency"], row["baselineMaxMs"], row["candidateMaxMs"]),
                         ("warm-burst", 4, 5100, 13))

    def test_invalid_wave_evidence_is_rejected(self):
        failed = wave("candidate", failed_slot=1)
        for item in (dict(failed, status="ok"), dict(failed, requests=failed["requests"][:-1]),
                     dict(failed, elapsedMs=10), dict(wave("candidate"), resultFingerprint="stale")):
            with self.subTest(item=item["status"], elapsed=item["elapsedMs"]), self.assertRaises(ValueError):
                analysis.validate_wave(item)
        mixed = wave("candidate", base="mixed")
        mixed["requests"][0]["scenario"] = mixed["requests"][1]["scenario"]
        with self.assertRaisesRegex(ValueError, "slot/role"):
            analysis.validate_wave(mixed)

    def test_shared_cache_observed_limits_and_failures_before_open(self):
        trial = dict(wave("candidate", failed_slot=0), sqliteCacheMode="Shared", requestedMmapLimitBytes=None)
        metadata = dict(sqliteCacheMode="Shared", requestedMmapLimitBytes=None, config=dict(cacheMode="Shared", mmapBytes=None))
        limit = 1024**3
        plan = dict(SqliteCacheMode="Shared", CandidateMmapProductionLimit=limit)
        for r in trial["requests"]:
            r["effectiveMmapLimitBytes"] = [] if r["status"] == "timeout" else [limit]
        analysis.validate_wave(trial)  # A timeout before the first open has no native readback.
        self.assertEqual(analysis.pressure_connection_issues(trial, metadata, plan), [])
        split_plan = dict(plan, BaselineSqliteCacheMode="Shared", CandidateSqliteCacheMode="Private")
        self.assertIn("cache mode", " ".join(analysis.pressure_connection_issues(trial, metadata, split_plan)))
        trial["sqliteCacheMode"] = metadata["sqliteCacheMode"] = metadata["config"]["cacheMode"] = "Private"
        self.assertEqual(analysis.pressure_connection_issues(trial, metadata, split_plan), [])
        baseline = dict(trial, variant="baseline", sqliteCacheMode="Shared")
        baseline_meta = dict(metadata, sqliteCacheMode="Shared", config=dict(cacheMode="Shared", mmapBytes=None))
        self.assertEqual(analysis.pressure_connection_issues(baseline, baseline_meta, split_plan), [])
        trial["sqliteCacheMode"] = metadata["sqliteCacheMode"] = metadata["config"]["cacheMode"] = "Shared"
        trial["requests"][1]["effectiveMmapLimitBytes"] = [limit, 0]
        self.assertIn("effective mmap limit", " ".join(analysis.pressure_connection_issues(trial, metadata, plan)))
        metadata["config"]["cacheMode"] = "Private"
        trial["requestedMmapLimitBytes"] = 0
        issues = " ".join(analysis.pressure_connection_issues(trial, metadata, plan))
        self.assertIn("cache mode", issues)
        self.assertIn("override", issues)
        trial["requests"][1]["effectiveMmapLimitBytes"] = []
        with self.assertRaisesRegex(ValueError, "slot 1"):
            analysis.validate_wave(trial)

    def test_mmap_role_override_uses_nested_request_role_and_zero_fallback(self):
        limit = 1024**3
        plan = dict(SqliteCacheMode="Private", MmapProductionLimit=0, MmapBytes=None,
                    CandidateMmapLimitsByRole={"exploration": limit, "records-attempts": limit})
        metadata = dict(sqliteCacheMode="Private", requestedMmapLimitBytes=None,
                        config=dict(cacheMode="Private", mmapBytes=None))
        for base in ("exploration", "mixed"):
            trial = dict(wave("candidate", base=base), sqliteCacheMode="Private", requestedMmapLimitBytes=None)
            for request in trial["requests"]:
                request["effectiveMmapLimitBytes"] = [plan["CandidateMmapLimitsByRole"].get(request["scenario"], 0)]
                audit.verify_mmap(request, "candidate", plan)
            self.assertEqual(analysis.pressure_connection_issues(trial, metadata, plan), [])
            request = trial["requests"][0]
            request["effectiveMmapLimitBytes"] = [0 if base == "exploration" else limit]
            self.assertIn("effective mmap limit", " ".join(analysis.pressure_connection_issues(trial, metadata, plan)))
            with self.assertRaisesRegex(ValueError, "Actual mmap limit"):
                audit.verify_mmap(request, "candidate", plan)
        baseline_request = dict(scenario="exploration", status="ok", effectiveMmapLimitBytes=[0])
        audit.verify_mmap(baseline_request, "baseline", plan)
        with self.assertRaises(ValueError):
            audit.verify_mmap(dict(baseline_request, effectiveMmapLimitBytes=[limit]), "baseline", plan)
        audit.verify_mmap(dict(baseline_request, status="timeout", effectiveMmapLimitBytes=[]), "candidate", plan)
        with self.assertRaisesRegex(ValueError, "observations"):
            audit.verify_mmap(dict(baseline_request, effectiveMmapLimitBytes=[]), "candidate", plan)

    def test_golden_checks_include_successful_siblings_of_failed_waves(self):
        baseline = wave("baseline", base="mixed", failed_slot=2)
        candidate = wave("candidate", base="mixed")
        key = ("history", "mixed.warm-burst", 10)
        groups = {key: {0: {"baseline": [baseline], "candidate": [candidate]}}}
        self.assertEqual(analysis.role_fingerprints(groups), [("history", 10, "records-history")])
        # No comparable successful top-level wave, but both builds returned the first request.
        baseline["requests"][0]["resultFingerprint"] = "different"
        with self.assertRaisesRegex(ValueError, "baseline/golden role"):
            analysis.role_fingerprints(groups)

    def test_budget_gate_verifies_full_planned_inventory_and_binary_and_copy_evidence(self):
        key = ("history", "imports-all.warm-burst", 10)
        groups = {key: {0: {v: [wave(v)] for v in analysis.VARIANTS}}}
        plan = dict(Blocks=1, Dimensions=["history"], Scales=[10], Scenarios=["imports-all"], Regimes=["warm-burst"],
                    BudgetMs=5000, Concurrency=4, DriverHash="driver", BaselineAssemblyHash="B", CandidateAssemblyHash="C")
        sources = {"campaign-plan.json": {"campaignPlan": plan}}
        for v, digest in (("baseline", "B"), ("candidate", "C")):
            sources[f"{v}-0.json"] = {"metadata": dict(driverHash="driver", productionHash=digest, fixture={"sourceSha256": "db"},
                config=dict(variant=v, block=0, dimension="history", scale=10, budgetMs=5000),
                copyChecks=[dict(scenario="imports-all", regime="warm-burst", copiedHash="db", matches=True)])}
        row = analysis.summarize_cell(key, groups[key], [], 1)
        self.assertTrue(analysis.candidate_budget_gate([row], groups, sources, 1, [])["passed"])
        config = sources["candidate-0.json"]["metadata"]["config"]
        config["diagnostic"] = True
        self.assertIn("diagnostic instrumentation", " ".join(analysis.candidate_budget_gate([row], groups, sources, 1, [])["problems"]))
        config["diagnostic"] = False
        request = groups[key][0]["candidate"][0]["requests"][0]
        request["diagnostics"] = []  # An empty instrumented queue still means the timed path was changed.
        self.assertIn("diagnostic instrumentation", " ".join(analysis.candidate_budget_gate([row], groups, sources, 1, [])["problems"]))
        request["diagnostics"] = None
        self.assertTrue(analysis.candidate_budget_gate([row], groups, sources, 1, [])["passed"])
        self.assertFalse(analysis.candidate_budget_gate([row], groups, sources, 1, [("history", 10, "imports-all")])["passed"])
        sources["candidate-0.json"]["metadata"]["copyChecks"][0]["matches"] = False
        plan["Scales"] = [10, 100]
        reasons = " ".join(analysis.candidate_budget_gate([row], groups, sources, 1, [])["problems"])
        self.assertIn("inventory", reasons)
        self.assertIn("fixture hash", reasons)

    def test_variant_specific_indexed_fixtures_and_actual_gc_mode_must_match_plan(self):
        scratch = Path(__file__).resolve().parents[1] / "artifacts" / "database-load-analysis-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temp:
            key = ("history", "imports-all.warm-burst", 10)
            groups = {key: {0: {v: [wave(v)] for v in analysis.VARIANTS}}}
            plan = dict(Blocks=1, Dimensions=["history"], Scales=[10], Scenarios=["imports-all"], Regimes=["warm-burst"],
                        BudgetMs=5000, Concurrency=4, DriverHash="driver", GcMode="Server")
            sources = {"campaign-plan.json": {"campaignPlan": plan}}
            for v, digest in (("baseline", "old-indexes"), ("candidate", "new-indexes")):
                directory = Path(temp) / v
                directory.mkdir()
                manifest = directory / "history-10.json"
                contents = dict(dimension="history", scale=10, sourceSha256=digest, counts={"Activities": 1000},
                                ownerId="owner", ownerCount=500)
                manifest.write_text(json.dumps(contents), encoding="utf-8")
                plan[v.title() + "FixtureRoot"] = plan[v.title() + "MetadataRoot"] = str(directory)
                plan[v.title() + "AssemblyHash"] = v
                config = dict(variant=v, block=0, dimension="history", scale=10, budgetMs=5000,
                              fixtureDirectory=str(directory / "history-10"), metadataFile=str(manifest))
                sources[f"{v}-0.json"] = {"metadata": dict(driverHash="driver", productionHash=v, config=config,
                    serverGc=True, fixture=contents, copyChecks=[dict(scenario="imports-all", regime="warm-burst", targetKind="validated-warm-source",
                                                                     copiedHash=digest, matches=True)])}
            row = analysis.summarize_cell(key, groups[key], [], 1)
            results = analysis.candidate_budget_gate([row], groups, sources, 1, [])
            self.assertTrue(results["passed"], results["problems"])
            self.assertEqual(len(results["verifiedManifests"]), 2)
            candidate = sources["candidate-0.json"]["metadata"]
            candidate["copyChecks"][0]["targetKind"] = "unbuffered-copy"
            reasons = " ".join(analysis.candidate_budget_gate([row], groups, sources, 1, [])["problems"])
            self.assertIn("target kind", reasons)
            candidate["serverGc"] = False
            candidate["fixture"]["sourceSha256"] = candidate["copyChecks"][0]["copiedHash"] = "different"
            reasons = " ".join(analysis.candidate_budget_gate([row], groups, sources, 1, [])["problems"])
            self.assertIn("GC mode", reasons)
            self.assertIn("planned manifest", reasons)

    def test_exact_two_sided_sign_test_ignores_ties(self):
        self.assertEqual(analysis.exact_sign_test([1] * 16 + [0]), 2 / 2**16)
        self.assertEqual(analysis.exact_sign_test([-1] * 15 + [1]), 2 * 17 / 2**16)
        self.assertEqual(analysis.exact_sign_test([0] * 16), 1)

    def test_paired_ratios_intervals_and_holm_family(self):
        faster, exclusions = summarize(paired_blocks())
        slower, _ = summarize(paired_blocks(20), "map")
        mixed = paired_blocks()
        mixed[0]["candidate"][0]["elapsedMs"] = 20
        mostly_faster, _ = summarize(mixed, "activity")
        tied, _ = summarize(paired_blocks(10), "tags")
        self.assertEqual(analysis.classify([mostly_faster, slower, tied, faster]), 8)
        self.assertEqual(exclusions, [])
        self.assertEqual(faster["pairedBlocks"], 16)
        self.assertEqual((faster["candidateMaxMs"], faster["candidateHeadroomMs"]), (5, 4995))
        self.assertEqual((faster["speedup"], faster["ciLow"], faster["ciHigh"]), (2, 2, 2))
        self.assertEqual([r["holmP"] for r in (faster, slower, mostly_faster, tied)],
                         [8 * 2 / 2**16, 8 * 2 / 2**16, 6 * 34 / 2**16, 1])
        self.assertEqual([r["conclusion"] for r in (faster, slower, mostly_faster, tied)],
                         ["faster", "slower", "faster", "inconclusive"])

    def test_missing_both_variants_prevents_conclusion(self):
        blocks = paired_blocks()
        del blocks[7]
        row, problems = summarize(blocks)
        analysis.classify([row])
        self.assertEqual((row["pairedBlocks"], row["blocks"], row["excludedBlocks"]), (15, 16, 1))
        self.assertEqual(row["conclusion"], "incomplete comparison")
        self.assertIsNone(row["holmP"])
        self.assertEqual((row["failurePairedBlocks"], row["failureP"], row["failureConclusion"]),
                         (15, None, "incomplete comparison"))
        self.assertEqual(problems[0]["block"], 7)
        self.assertIn("missing baseline", problems[0]["reason"])
        self.assertIn("missing candidate", problems[0]["reason"])

    def test_censoring_mismatch_and_duplicate_processes_exclude_entire_blocks(self):
        blocks = paired_blocks()
        blocks[2]["candidate"][0].update(status="timeout", elapsedMs=8000, budgetMs=5000)
        blocks[3]["candidate"][0].update(resultFingerprint="different-result", elapsedMs=0.001)
        blocks[4]["candidate"].append(dict(blocks[4]["candidate"][0], source="repeated-process.json"))
        row, problems = summarize(blocks)
        analysis.classify([row])
        self.assertEqual((row["candidateFailedSamples"], row["pairedBlocks"], row["excludedBlocks"]), (1, 13, 3))
        self.assertEqual((row["budgetMs"], row["candidateMaxMs"], row["candidateHeadroomMs"]), (5000, 8000, -3000))
        self.assertEqual((row["speedup"], row["conclusion"], row["holmP"]), (2, "incomplete comparison", None))
        self.assertEqual((row["failurePairedBlocks"], row["failureRegressedBlocks"], row["failureHolmP"]), (14, 1, None))
        reasons = {p["block"]: p["reason"] for p in problems}
        self.assertIn("timeout", reasons[2])
        self.assertIn("5000 ms", reasons[2])
        self.assertIn("fingerprint mismatch", reasons[3])
        self.assertIn("multiple process files", reasons[4])

    def test_paired_failures_remain_observable_when_latency_is_censored(self):
        improved = paired_blocks()
        regressed = paired_blocks()
        for block in range(16):
            improved[block]["baseline"][0].update(status="timeout", resultFingerprint=None)
            regressed[block]["candidate"][0].update(status="budget-exceeded", elapsedMs=6000)
        # A concordant failure adds to both totals but supplies no direction for McNemar's test.
        improved[15]["candidate"][0].update(status="error", resultFingerprint=None)
        fewer, problems = summarize(improved, "fewer")
        more, _ = summarize(regressed, "more")
        self.assertEqual(analysis.classify([fewer, more]), 4)
        self.assertTrue(all(p["failurePairUsable"] and not p["latencyPairUsable"] for p in problems))
        self.assertEqual((fewer["baselineFailedBlocks"], fewer["candidateFailedBlocks"],
                          fewer["failureImprovedBlocks"], fewer["failureRegressedBlocks"]), (16, 1, 15, 0))
        self.assertEqual((fewer["speedup"], fewer["holmP"], fewer["conclusion"]), (None, None, "incomplete comparison"))
        self.assertEqual((fewer["failureP"], fewer["failureHolmP"], fewer["failureConclusion"]),
                         (2 / 2**15, 3 * 2 / 2**15, "fewer failures"))
        self.assertEqual((more["failureP"], more["failureHolmP"], more["failureConclusion"]),
                         (2 / 2**16, 4 * 2 / 2**16, "more failures"))

    def test_failure_inference_requires_matching_budgets_and_available_results(self):
        for change in ({"budgetMs": 100}, {"resultFingerprint": "changed"}):
            with self.subTest(change=change):
                blocks = paired_blocks()
                blocks[0]["baseline"][0].update(status="budget-exceeded", elapsedMs=6000, **change)
                row, problems = summarize(blocks)
                analysis.classify([row])
                self.assertIsNone(row["failureP"])
                self.assertEqual(row["failureConclusion"], "incomplete comparison")
                self.assertFalse(problems[0]["failurePairUsable"])
                if "budgetMs" in change:
                    self.assertIsNone(row["candidateHeadroomMs"])

    def test_diagnostic_variants_preserve_evidence_without_adding_trials(self):
        scratch = Path(__file__).resolve().parents[1] / "artifacts" / "database-load-analysis-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as directory:
            directory = Path(directory)
            for variant in ("baseline", "candidate", "diagnostic-baseline", "diagnostic-candidate"):
                document = {"metadata": {"variant": variant}, "samples": [sample(variant, 0, 50)]}
                (directory / f"{variant}.json").write_text(json.dumps(document), encoding="utf-8")
            plan = {"BaselineAssemblyHash": "baseline-build", "CandidateAssemblyHash": "candidate-build", "Blocks": 16}
            (directory / "campaign-plan.json").write_text(json.dumps(plan), encoding="utf-8")
            groups, provenance, diagnostics = analysis.read_trials(directory)
            self.assertEqual(provenance["campaign-plan.json"], {"campaignPlan": plan})
            for name, args, label in (("original", [], "post hoc failure endpoint"),
                                      ("follow-up", ["--failure-endpoint", "prespecified"], "prespecified before this comparison")):
                output = directory / name
                subprocess.run([sys.executable, "-B", str(Path(analysis.__file__)), "--input", str(directory),
                                "--output", str(output), *args], check=True, capture_output=True)
                for report in ("report.md", "report.html"):
                    self.assertIn(label, (output / report).read_text(encoding="utf-8"))
        trials = groups[("history", "imports", 10)][0]
        self.assertEqual({v: len(s) for v, s in trials.items()}, {"baseline": 1, "candidate": 1})
        self.assertEqual(set(diagnostics), {"diagnostic-baseline.json", "diagnostic-candidate.json"})
        self.assertEqual(len(provenance), 5)


if __name__ == "__main__":
    unittest.main()
