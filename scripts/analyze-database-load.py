"""Summarize paired baseline and candidate database load trials using Python's standard library."""

import argparse
import csv
import hashlib
import html
import json
import math
import random
from collections import Counter, defaultdict
from datetime import datetime, timedelta
from pathlib import Path
from statistics import median


VARIANTS = ("baseline", "candidate")
FAILURE_STATUSES = {"timeout", "budget-exceeded", "error"}
MIN_BLOCKS = 12
BOOTSTRAPS = 10_000
SEED = 817143
PRESSURE_CLIENTS = {"cold-solo": 1, "warm-burst": 4, "cold-burst": 4}
MIXED_REQUESTS = ("records-period-streams", "records-attempts", "records-history", "imports-all")


def finite_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def valid_mmap_observations(request):
    limits = request.get("effectiveMmapLimitBytes")
    needs_readback = status(request) == "ok"
    return (isinstance(limits, list) and all(type(v) is int and v >= 0 for v in limits)
            and (bool(limits) or not needs_readback))


def validate_wave(sample):
    """Verify one pressure measurement from its individual client outcomes."""
    base, _, regime = sample["scenario"].rpartition(".")
    if regime not in PRESSURE_CLIENTS and "requests" not in sample and "regime" not in sample:
        return
    cache_mode = "warm-sqlite-os-uncontrolled" if regime == "warm-burst" else "unbuffered-file-first-touch"
    if (regime not in PRESSURE_CLIENTS or sample.get("regime") != regime or sample.get("baseScenario") != base
            or not base or sample.get("cacheMode") != cache_mode):
        raise ValueError("invalid pressure regime, base scenario or cache mode")
    clients = PRESSURE_CLIENTS[regime]
    requests = sample.get("requests")
    if (type(sample.get("concurrency")) is not int or sample["concurrency"] != clients
            or not isinstance(requests, list) or len(requests) != clients):
        raise ValueError(f"{regime} requires {clients} individual requests")
    roles = MIXED_REQUESTS if base == "mixed" else (base,) * clients
    if len(roles) != clients:
        raise ValueError("mixed requires a four-client burst")
    budget = sample.get("budgetMs")
    if not finite_number(budget) or budget <= 0:
        raise ValueError("pressure sample requires a positive budget")
    failed = False
    slots = {}
    for request in requests:
        slot = request["slot"]
        if type(slot) is not int or not 0 <= slot < clients or slot in slots or request.get("scenario") != roles[slot]:
            raise ValueError("missing, duplicate or incorrect request slot/role")
        slots[slot] = request
        elapsed = request.get("elapsedMs")
        entered = request.get("enteredMs")
        if not finite_number(elapsed) or not finite_number(entered) or not 0 <= entered <= elapsed:
            raise ValueError(f"invalid common-arrival timing for slot {slot}")
        outcome = status(request)
        if outcome == "ok" and elapsed >= budget:
            raise ValueError(f"slot {slot} marked successful at or beyond budget")
        if outcome not in FAILURE_STATUSES and outcome != "ok":
            raise ValueError(f"invalid slot {slot} outcome: {outcome}")
        if "sqliteCacheMode" in sample and not valid_mmap_observations(request):
            raise ValueError(f"invalid or missing SQLite mmap limit readback for slot {slot}")
        failed |= outcome != "ok"
    if sample.get("elapsedMs") != max(r["elapsedMs"] for r in requests):
        raise ValueError("wave elapsed time differs from slowest request")
    if failed:
        if sample.get("status") not in FAILURE_STATUSES or sample.get("resultFingerprint") is not None:
            raise ValueError("wave masks a failed request or supplies a successful fingerprint")
    else:
        parts = [f"{i}|{slots[i]['scenario']}|{slots[i]['resultFingerprint']}" for i in range(clients)]
        digest = hashlib.sha256("\n".join(parts).encode()).hexdigest().upper()
        if status(sample) != "ok" or sample["resultFingerprint"] != digest:
            raise ValueError("wave status or fingerprint differs from successful request slots")


def read_trials(directory):
    groups = defaultdict(lambda: defaultdict(lambda: defaultdict(list)))
    sources = {}
    diagnostics = {}
    for path in sorted(directory.rglob("*.json")):
        with path.open(encoding="utf-8-sig") as stream:
            document = json.load(stream)
        if path.name in ("campaign-plan.json", "diagnostic-campaign-plan.json") and isinstance(document, dict):
            sources[path.relative_to(directory).as_posix()] = {"campaignPlan": document}
            continue
        if not isinstance(document, dict) or "samples" not in document:
            continue
        if not isinstance(document["samples"], list):
            raise ValueError(f"{path}: samples must be an array")
        source = path.relative_to(directory).as_posix()
        sources[source] = {k: v for k, v in document.items() if k != "samples"}
        for index, sample in enumerate(document["samples"]):
            if sample.get("warmup") or sample.get("phase") == "warmup":
                continue
            try:
                variant = sample["variant"].lower()
                if variant in ("diagnostic-baseline", "diagnostic-candidate"):
                    diagnostics.setdefault(source, []).append(sample)
                    continue
                if variant not in VARIANTS:
                    raise ValueError(f"unknown variant {variant!r}")
                scale = int(sample["scale"])
                block = int(sample["block"])
                if scale < 1 or block < 0 or str(block) != str(sample["block"]) or str(scale) != str(sample["scale"]):
                    raise ValueError("scale and block must be positive and nonnegative integers respectively")
                key = (sample["dimension"], sample["scenario"], scale)
                if not all(isinstance(v, str) and v.strip() for v in key[:2]):
                    raise ValueError("dimension and scenario must have names")
                sample = dict(sample, source=source)
                for name in ("budgetMs", "timeoutMs"):
                    if name not in sample and name in document:
                        sample[name] = document[name]
                validate_wave(sample)
                groups[key][block][variant].append(sample)
            except (AttributeError, KeyError, TypeError, ValueError) as exc:
                raise ValueError(f"{path}, sample {index}: {exc}") from exc
    if not groups:
        raise ValueError(f"No timed samples found in {directory}")
    return groups, sources, diagnostics


def status(sample):
    value = str(sample.get("status", "missing_status")).lower()
    if value not in ("ok", "success"):
        return value
    if not finite_number(sample.get("elapsedMs")) or sample["elapsedMs"] <= 0:
        return "invalid_elapsed"
    if not sample.get("resultFingerprint"):
        return "missing_fingerprint"
    return "ok"


def role_fingerprints(groups, golden_paths=()):
    """Check successful client responses even when another slot in the wave failed."""
    baseline = defaultdict(set)
    candidate = defaultdict(set)
    for (dimension, _, scale), blocks in groups.items():
        for arms in blocks.values():
            for variant, samples in arms.items():
                for sample in samples:
                    for request in sample.get("requests", (sample,)):
                        if status(request) == "ok":
                            key = (dimension, scale, request["scenario"])
                            target = baseline if variant == "baseline" else candidate
                            target[key].add(request["resultFingerprint"])
    for path in golden_paths:
        files = sorted(path.rglob("samples.json")) if path.is_dir() else [path]
        if not files:
            raise ValueError(f"No golden samples in {path}")
        for file in files:
            document = json.loads(file.read_text(encoding="utf-8-sig"))
            if isinstance(document, list):
                for row in document:  # Explicit records supplied by the caller as golden results.
                    baseline[row["dimension"], row["scale"], row["scenario"]].add(row["resultFingerprint"])
            else:
                for sample in document.get("samples", ()):
                    if sample.get("variant") not in ("baseline", "diagnostic-baseline"):
                        continue
                    validate_wave(sample)
                    for request in sample.get("requests", (sample,)):
                        if status(request) == "ok":
                            key = (sample["dimension"], sample["scale"], request["scenario"])
                            baseline[key].add(request["resultFingerprint"])
    missing = []
    for key, observed in candidate.items():
        if len(observed | baseline[key]) != 1:
            raise ValueError(f"{key}: candidate result fingerprint differs across requests or from baseline/golden role")
        if not baseline[key]:
            missing.append(key)
    return sorted(missing)


def normalized_fixture(contents):
    # Older manifests omit these optional fields; the new C# record serializes defaults.
    return {"sourcePath": None, "primed": False, "derivedTables": None, **contents}


def derived_summary_issues(contents, variant, plan):
    specifications = (("DerivedSummaryVersion", "activityRecordEffortSummaries", "ActivityStreams", "sourceStreamCount"),
                      ("ExplorationCellSetVersion", "activityExplorationCellSets", "ActivityExplorationIndexes", "sourceIndexCount"),
                      ("ExplorationAggregateVersion", "activityExplorationAggregateRows", "Activities", "sourceActivityCount"),
                      ("PeriodRecordRowVersion", "activityPeriodRecordRows", "Activities", "sourceActivityCount"))
    planned = [(plan.get(variant.title() + field), name, source, source_field)
               for field, name, source, source_field in specifications if plan.get(variant.title() + field) is not None]
    derived = contents.get("derivedTables")
    if not planned:
        return ["unplanned derived summary table"] if derived is not None else []
    if any(type(version) is not int or version <= 0 for version, _, _, _ in planned):
        return ["invalid planned derived summary version"]
    if not isinstance(derived, dict) or set(derived) != {name for _, name, _, _ in planned}:
        return ["missing or unexpected derived summary manifest"]
    for version, name, source, source_field in planned:
        summary = derived[name]
        count = contents.get("counts", {}).get(source)
        if (not isinstance(summary, dict) or type(count) is not int or count < 0
                or any(type(summary.get(k)) is not int for k in ("rowCount", source_field, "invalidRows", "computationVersion"))
                or summary["rowCount"] != count or summary[source_field] != count
                or summary["invalidRows"] != 0 or summary["computationVersion"] != version):
            return [f"{name}: derived summary coverage, source integrity or version differs from plan"]
    return []


def planned_pressure_fixtures(plan, problems):
    """Resolve each declared fixture's small setup manifest; never open the benchmark DB."""
    fixtures = {}
    split = any(v.title() + suffix in plan for v in VARIANTS for suffix in ("FixtureRoot", "MetadataRoot"))
    if not split and "MetadataRoot" not in plan:
        return fixtures
    for variant in VARIANTS:
        prefix = variant.title() if split else ""
        fixture_root, metadata_root = plan.get(prefix + "FixtureRoot"), plan.get(prefix + "MetadataRoot")
        if not fixture_root or not metadata_root:
            problems.append(f"{variant}: missing planned fixture or metadata root")
            continue
        for dimension in plan.get("Dimensions", ()):
            for scale in plan.get("Scales", ()):
                name = f"{dimension}-{scale}"
                if Path(name).name != name:
                    raise ValueError("invalid fixture name in pressure plan")
                directory = (Path(fixture_root) / name).resolve()
                manifest = (Path(metadata_root) / (name + ".json")).resolve()
                try:
                    contents = json.loads(manifest.read_text(encoding="utf-8-sig"))
                except (OSError, ValueError) as exc:
                    problems.append(f"{variant}/{name}: cannot read planned fixture manifest ({type(exc).__name__})")
                    continue
                if (contents.get("dimension") != dimension or contents.get("scale") != scale
                        or not contents.get("sourceSha256") or not contents.get("ownerId")
                        or not contents.get("counts") or contents.get("ownerCount") is None):
                    problems.append(f"{variant}/{name}: invalid fixture manifest")
                    continue
                problems.extend(f"{variant}/{name}: {issue}" for issue in derived_summary_issues(contents, variant, plan))
                fixtures[variant, dimension, scale] = (directory, manifest, contents)
    for variant, dimension, scale in fixtures:
        if variant != "candidate" or ("baseline", dimension, scale) not in fixtures:
            continue
        left = fixtures["baseline", dimension, scale][2]
        right = fixtures["candidate", dimension, scale][2]
        if any(left[k] != right[k] for k in ("ownerId", "ownerCount", "counts")):
            problems.append(f"{dimension}/{scale}: fixture ownership or table counts differ between variants")
    return fixtures


def pressure_connection_issues(sample, metadata, plan):
    prefix = sample["variant"].title()
    cache = plan.get(prefix + "SqliteCacheMode", plan.get("SqliteCacheMode", plan.get("CacheMode")))
    if cache is None and "sqliteCacheMode" not in sample:
        return []
    config = metadata.get("config", {})
    problems = []
    if (cache not in ("Shared", "Private", "Default") or sample.get("sqliteCacheMode") != cache
            or metadata.get("sqliteCacheMode") != cache or config.get("cacheMode") != cache):
        problems.append("SQLite connection cache mode differs from plan")
    requested = plan.get(prefix + "MmapBytes", plan.get("MmapBytes"))
    if ("requestedMmapLimitBytes" not in sample or sample["requestedMmapLimitBytes"] != requested
            or metadata.get("requestedMmapLimitBytes") != requested or config.get("mmapBytes") != requested):
        problems.append("SQLite mmap override differs from plan")
    required = plan.get(prefix + "MmapProductionLimit", plan.get("MmapProductionLimit"))
    if required is not None and (type(required) is not int or required < 0):
        problems.append("invalid planned effective mmap limit")
    by_role = plan.get(prefix + "MmapLimitsByRole", {})
    if (not isinstance(by_role, dict) or any(not isinstance(role, str) or not role
            or type(limit) is not int or limit < 0 for role, limit in by_role.items())):
        problems.append("invalid planned per-role mmap limits")
        by_role = {}
    for slot in sample["requests"]:
        limits = slot.get("effectiveMmapLimitBytes")
        role_required = by_role.get(slot["scenario"], required)
        if not valid_mmap_observations(slot):
            problems.append(f"slot {slot['slot']} lacks valid mmap limit observations")
        elif role_required is not None and any(limit != role_required for limit in limits):
            problems.append(f"slot {slot['slot']} effective mmap limit differs from plan")
    return problems


def planned_primers(plan, problems):
    primers = {}
    paths = plan.get("RuntimeMetadataFiles") or []
    if not paths:
        return primers
    if (len(paths) != 2 or plan.get("RuntimePrimeIterations") != 2 or plan.get("RuntimePrimeConcurrency") != 4
            or plan.get("WarmupBudgetMs") != 60_000):
        problems.append("runtime priming requires two independent fixtures, two four-client waves each and a 60000 ms budget")
    for path in paths:
        manifest = Path(path).resolve()
        try:
            data = json.loads(manifest.read_text(encoding="utf-8-sig"))
        except (OSError, ValueError) as exc:
            problems.append(f"cannot read planned priming manifest {manifest} ({type(exc).__name__})")
            continue
        if (data.get("primed") is not True or not data.get("sourcePath") or not data.get("ownerId")
                or not data.get("sourceSha256") or data.get("dimension") != "history"):
            problems.append(f"invalid independent priming fixture manifest: {manifest}")
            continue
        # Both arms prime on the same declared runtime fixtures, including candidate-derived rows.
        problems.extend(f"runtime primer {manifest}: {issue}" for issue in derived_summary_issues(data, "candidate", plan))
        primers[manifest] = data
    if ({v.get("scale") for v in primers.values()} != {1, 100}
            or len({Path(v["sourcePath"]).resolve() for v in primers.values()}) != 2
            or len({v["sourceSha256"] for v in primers.values()}) != 2):
        problems.append("runtime priming requires distinct sparse 1x and dense 100x history fixtures")
    return primers


def utc_stamp(text):
    value = datetime.fromisoformat(text.replace("Z", "+00:00"))
    if value.utcoffset() is None:
        raise ValueError("timestamp lacks time zone")
    return value


def host_readiness_issues(samples, metadata, plan):
    policy = plan.get("HostGate")
    evidence = metadata.get("hostReadiness", [])
    if metadata.get("config", {}).get("hostGate") != policy:
        return ["host readiness policy differs from plan"]
    if policy is None:
        return ["unplanned host readiness gating"] if evidence else []
    keys = ("pollMs", "consecutiveSamples", "maxCurrentDiskQueue", "maxAverageDiskQueue", "maxCpuPercent", "maxWaitMs")
    if (not isinstance(policy, dict) or set(policy) != set(keys)
            or any(not finite_number(policy[k]) or policy[k] < 0 for k in keys)
            or any(type(policy[k]) is not int for k in ("pollMs", "consecutiveSamples", "maxWaitMs"))
            or policy["pollMs"] < 250 or policy["consecutiveSamples"] < 2 or policy["maxCpuPercent"] > 100
            or policy["maxWaitMs"] <= policy["pollMs"] * policy["consecutiveSamples"]):
        return ["invalid host readiness policy"]
    if not isinstance(evidence, list) or len(evidence) != len(samples):
        return ["missing host readiness evidence"]
    waves = {(sample["baseScenario"], sample["regime"]): sample for sample in samples}
    seen = set()
    try:
        for wait in evidence:
            key = (wait["scenario"], wait["regime"])
            if key in seen or key not in waves or wait["passed"] is not True:
                return ["duplicate, unknown or rejected host readiness window"]
            seen.add(key)
            arrival = utc_stamp(waves[key]["arrivalUtc"])
            start, end = utc_stamp(wait["startedUtc"]), utc_stamp(wait["completedUtc"])
            prior = [utc_stamp(prime["completedUtc"]) for prime in metadata.get("priming", ())
                     if (prime["scenario"], prime["regime"]) == key]
            if prior and max(prior) > start:
                return ["host readiness was sampled before runtime priming ended"]
            limit = timedelta(milliseconds=policy["maxWaitMs"] + 50)
            if not (start <= end <= arrival and end - start <= limit):
                return ["host readiness timing is inconsistent with the pre-arrival rule"]
            previous, consecutive = start, 0
            for observation in wait["observations"]:
                stamp = utc_stamp(observation["utc"])
                metrics = ("currentDiskQueue", "averageDiskQueue", "cpuPercent")
                thresholds = ("maxCurrentDiskQueue", "maxAverageDiskQueue", "maxCpuPercent")
                if (not previous + timedelta(milliseconds=policy["pollMs"] - 50) <= stamp <= end
                        or consecutive == policy["consecutiveSamples"]):
                    return ["invalid host readiness observation sequence"]
                previous = stamp
                error = observation.get("errorCode")
                if error is not None:
                    if (error not in {"0x800007D4", "0x800007D5", "0x800007D6", "0x800007D7", "0x800007D8", "0xC0000BBA"}
                            or any(observation[k] is not None for k in metrics)):
                        return ["invalid rejected host readiness observation"]
                    consecutive = 0
                    continue
                if (any(not finite_number(observation[k]) or observation[k] < 0 for k in metrics)
                        or observation["cpuPercent"] > 100):
                    return ["invalid host readiness counter observation"]
                consecutive = consecutive + 1 if all(observation[k] <= policy[t] for k, t in zip(metrics, thresholds)) else 0
            if consecutive != policy["consecutiveSamples"] or arrival - previous > timedelta(seconds=1):
                return ["host readiness release lacked a current passing sample window"]
    except (KeyError, TypeError, ValueError, AttributeError):
        return ["invalid host readiness evidence"]
    return []


def cold_stage_issues(samples, metadata, plan):
    staged = any(k in plan for k in ("ColdCopyWorkers", "ColdCopySettleMs"))
    if not staged and "ScenarioBatches" not in plan:
        return []
    scenarios = plan.get("Scenarios", ())
    if "ScenarioBatches" in plan:
        batches = plan["ScenarioBatches"]
        if (not isinstance(batches, list) or not batches or any(not isinstance(b, list) or not b for b in batches)
                or [s for b in batches for s in b] != scenarios or len(set(scenarios)) != len(scenarios)):
            return ["invalid scenario batches: ordered flattening must equal unique planned scenarios"]
        scenarios = metadata.get("config", {}).get("scenarios")
        if scenarios not in batches:
            return ["process scenarios do not exactly match an allowed ordered batch"]
    expected = {(s, r) for s in scenarios for r in plan.get("Regimes", ())
                if (s, r) != ("mixed", "cold-solo")}
    observed = [(s.get("baseScenario"), s.get("regime")) for s in samples]
    if len(observed) != len(expected) or set(observed) != expected:
        return ["incomplete or duplicate process workload for staged copies"]
    if not staged:
        return []
    workers, settle = plan.get("ColdCopyWorkers"), plan.get("ColdCopySettleMs")
    if type(workers) is not int or workers <= 0 or type(settle) is not int or settle < 0:
        return ["invalid planned cold copy workers or settle duration"]
    count = sum(r != "warm-burst" for _, r in expected)
    stage = metadata.get("coldPreparation")
    size = metadata.get("fixture", {}).get("databaseBytes")
    if not isinstance(stage, dict) or type(size) is not int or size <= 0:
        return ["missing cold staging or database size evidence"]
    if (any(type(stage.get(k)) is not int for k in ("replicaCount", "copyConcurrency", "requiredBytes", "freeBytes", "settleMs"))
            or stage["replicaCount"] != count or stage["copyConcurrency"] != min(workers, count)
            or stage["requiredBytes"] != size * count or stage["freeBytes"] < stage["requiredBytes"]
            or stage["settleMs"] != (settle if count else 0)):
        return ["cold replica count, worker cap, bytes, free space or settle duration differs from plan"]
    checks = metadata.get("copyChecks", ())
    if len(checks) != len(expected) or {(c.get("scenario"), c.get("regime")) for c in checks} != expected:
        return ["incomplete staged-copy integrity evidence"]
    tolerance = timedelta(milliseconds=20)  # UTC clock granularity, not a reduction in the requested delay.
    try:
        arrivals = [utc_stamp(s["arrivalUtc"]) for s in samples]
        finishes = [arrival + timedelta(milliseconds=s["elapsedMs"]) for arrival, s in zip(arrivals, samples)]
        if count:
            copied, ready = utc_stamp(stage["copiesReadyUtc"]), utc_stamp(stage["collectionReadyUtc"])
            if ready + tolerance < copied + timedelta(milliseconds=settle) or ready > min(arrivals) + tolerance:
                return ["cold copy settling or collection start violates stage ordering"]
        if any(utc_stamp(c["verifiedUtc"]) + tolerance < max(finishes) for c in checks):
            return ["fixture integrity read occurred before all measured waves finished"]
    except (KeyError, TypeError, ValueError, AttributeError, OverflowError):
        return ["missing or invalid cold staging, arrival or integrity timestamp"]
    return []


def priming_issues(sample, metadata, plan, primers):
    configured = [Path(p).resolve() for p in (metadata.get("config", {}).get("runtimeMetadataFiles") or [])]
    if not plan.get("RuntimeMetadataFiles"):
        return ["unplanned runtime priming"] if configured or metadata.get("priming") else []
    if list(primers) != configured:
        return ["runtime priming manifests differ from plan"]
    measured = metadata.get("fixture", {})
    measured_path = measured.get("sourcePath") or str(Path(metadata.get("config", {}).get("fixtureDirectory", "")) /
                                                     f"load-{sample['dimension']}-{sample['scale']}.db")
    if measured.get("primed", False):
        return ["measured fixture is marked as a runtime primer"]
    for fixture in primers.values():
        if (fixture["sourceSha256"] == measured.get("sourceSha256") or fixture["ownerId"] == measured.get("ownerId")
                or Path(fixture["sourcePath"]).resolve() == Path(measured_path).resolve()):
            return ["priming fixture is not independent of the measured database"]
    allowed_cases = {(base, regime) for base in plan.get("Scenarios", ()) for regime in plan.get("Regimes", ())
                     if (base, regime) != ("mixed", "cold-solo")}
    if any((e.get("scenario"), e.get("regime")) not in allowed_cases for e in metadata.get("priming", ())):
        return ["unplanned runtime priming workload"]
    events = [e for e in metadata.get("priming", ())
              if e.get("scenario") == sample["baseScenario"] and e.get("regime") == sample["regime"]]
    expected = [(Path(v["sourcePath"]).resolve(), v["scale"], i) for v in primers.values() for i in range(2)]
    observed = [(Path(e.get("sourcePath", "")).resolve(), e.get("scale"), e.get("iteration")) for e in events]
    if observed != expected:
        return ["missing, duplicate, out-of-order or incorrect priming fixture/iteration"]
    if any(e.get("concurrency") != 4 or e.get("budgetMs") != 60_000 for e in events):
        return ["runtime priming concurrency or budget differs from plan"]
    if "RuntimePrimeSqliteCacheMode" in plan:
        mode = plan["RuntimePrimeSqliteCacheMode"]
        if mode not in ("Shared", "Private", "Default") or any(e.get("sqliteCacheMode") != mode for e in events):
            return ["runtime priming SQLite cache mode differs from plan"]
    try:
        arrival = utc_stamp(sample["arrivalUtc"])
        previous = None
        for event in events:
            start, end = utc_stamp(event["startedUtc"]), utc_stamp(event["completedUtc"])
            if end < start or end > arrival or previous is not None and previous > start:
                return ["runtime priming overlaps or completes after measured arrival"]
            previous = end
    except (KeyError, TypeError, ValueError, AttributeError):
        return ["missing or invalid runtime priming or measured arrival timestamp"]
    return []


def candidate_budget_gate(rows, groups, sources, expected_blocks, missing_roles):
    problems = []
    fixtures = {}
    primers = {}
    gc_mode = None
    expected_processes = None
    by_source = defaultdict(list)
    if not expected_blocks:
        problems.append("strict gate requires --expected-blocks")
    plans = [v["campaignPlan"] for source, v in sources.items() if Path(source).name == "campaign-plan.json"]
    if len(plans) != 1:
        problems.append("strict gate requires exactly one campaign-plan.json")
    else:
        plan = plans[0]
        if expected_blocks != plan.get("Blocks"):
            problems.append("expected block count differs from plan")
        cases = plan.get("Scenarios", [])
        if not cases or not plan.get("Dimensions") or not plan.get("Scales"):
            problems.append("strict gate requires explicit scenarios, dimensions and scales in the plan")
        if "Regimes" in plan:
            fixtures = planned_pressure_fixtures(plan, problems)
            primers = planned_primers(plan, problems)
            if "GcMode" in plan:
                gc_mode = str(plan["GcMode"]).lower()
                if gc_mode not in ("server", "workstation"):
                    problems.append("invalid GC mode in pressure plan")
            elif any(v.title() + "FixtureRoot" in plan for v in VARIANTS):
                problems.append("per-variant fixture plan must specify GC mode")
            if not plan["Regimes"] or set(plan["Regimes"]) - PRESSURE_CLIENTS.keys() or plan.get("Concurrency") != 4:
                problems.append("invalid pressure regimes or concurrency in plan")
            cases = [f"{base}.{regime}" for base in cases for regime in plan["Regimes"]
                     if (base, regime) != ("mixed", "cold-solo")]
        expected = {(dim, case, scale) for dim in plan.get("Dimensions", ()) for case in cases for scale in plan.get("Scales", ())}
        if set(groups) != expected:
            problems.append(f"workload inventory differs from plan; missing {sorted(expected-set(groups))}, extra {sorted(set(groups)-expected)}")
        fixture_hashes = defaultdict(set)
        for blocks in groups.values():
            for arms in blocks.values():
                for samples in arms.values():
                    for sample in samples:
                        by_source[sample["source"]].append(sample)
        for source, samples in by_source.items():
            metadata = sources[source].get("metadata", {})
            problems.extend(f"{source}: {issue}" for issue in cold_stage_issues(samples, metadata, plan))
            problems.extend(f"{source}: {issue}" for issue in host_readiness_issues(samples, metadata, plan))
        if isinstance(plan.get("ScenarioBatches"), list):
            expected_processes = len(plan.get("Dimensions", ())) * len(plan.get("Scales", ())) * plan["Blocks"] * len(plan["ScenarioBatches"]) * 2
            if len(by_source) != expected_processes:
                problems.append(f"timed process count {len(by_source)} differs from declared batch plan {expected_processes}")
        for key, blocks in groups.items():
            for block, arms in blocks.items():
                for variant, samples in arms.items():
                    if len(samples) != plan.get("Repetitions", 1):
                        problems.append(f"{key} block {block}: incorrect {variant} repetition count")
                    for s in samples:
                        if s.get("budgetMs", s.get("timeoutMs")) != plan.get("BudgetMs"):
                            problems.append(f"{s['source']}: sample budget differs from plan")
                        if "Regimes" in plan:
                            metadata = sources[s["source"]].get("metadata", {})
                            config = metadata.get("config", {})
                            if (config.get("diagnostic", False) is not False or s.get("diagnostics") is not None
                                    or any(r.get("diagnostics") is not None for r in s.get("requests", ()))):
                                problems.append(f"{s['source']}: diagnostic instrumentation present in formal comparison")
                            for issue in pressure_connection_issues(s, metadata, plan):
                                problems.append(f"{s['source']}: {s['scenario']} {issue}")
                            for issue in priming_issues(s, metadata, plan, primers):
                                problems.append(f"{s['source']}: {s['scenario']} {issue}")
                            if gc_mode is not None and metadata.get("serverGc") is not (gc_mode == "server"):
                                problems.append(f"{s['source']}: actual server GC mode differs from plan")
                            if any(config.get(k) != s.get(k) for k in ("variant", "block", "dimension", "scale", "budgetMs")):
                                problems.append(f"{s['source']}: sample differs from process configuration")
                            if (not plan.get("DriverHash") or metadata.get("driverHash") != plan["DriverHash"]
                                    or not plan.get(variant.title() + "AssemblyHash")
                                    or metadata.get("productionHash") != plan[variant.title() + "AssemblyHash"]):
                                problems.append(f"{s['source']}: driver or production hash differs from plan")
                            source_hash = metadata.get("fixture", {}).get("sourceSha256")
                            declared = fixtures.get((variant, s["dimension"], s["scale"]))
                            directory = None
                            if declared:
                                directory, manifest, contents = declared
                                if (Path(config.get("fixtureDirectory", "")).resolve() != directory
                                        or Path(config.get("metadataFile", "")).resolve() != manifest
                                        or normalized_fixture(metadata.get("fixture", {})) != normalized_fixture(contents)):
                                    problems.append(f"{s['source']}: fixture path, metadata path or contents differ from planned manifest")
                            fixture_hashes[(directory, s["dimension"], s["scale"])].add(source_hash)
                            checks = [c for c in metadata.get("copyChecks", [])
                                      if c.get("scenario") == s.get("baseScenario") and c.get("regime") == s.get("regime")]
                            if (not source_hash or len(checks) != 1 or checks[0].get("matches") is not True
                                    or checks[0].get("copiedHash") != source_hash):
                                problems.append(f"{s['source']}: {s['scenario']} lacks a matching post-measurement fixture hash")
                            elif gc_mode is not None or "targetKind" in checks[0]:
                                cold = s["regime"] != "warm-burst"
                                expected_kind = "unbuffered-copy" if cold else "validated-warm-source"
                                if checks[0].get("targetKind") != expected_kind:
                                    problems.append(f"{s['source']}: {s['scenario']} target kind differs from {expected_kind}")
                                if cold:
                                    flags = {flag.strip() for flag in metadata.get("copyFlags", "").split("|")}
                                    if not {"COPY_FILE_NO_BUFFERING", "COPY_FILE_FAIL_IF_EXISTS"} <= flags:
                                        problems.append(f"{s['source']}: cold copy flags missing")
        for fixture, hashes in fixture_hashes.items():
            if len(hashes) != 1:
                problems.append(f"{fixture}: multiple source databases")
    for r in rows:
        if (r["failurePairedBlocks"] != expected_blocks or r["candidateFailedRequests"] or
                r["candidateHeadroomMs"] is None or r["candidateHeadroomMs"] <= 0):
            problems.append(f"{r['dimension']}/{r['scenario']}/{r['scale']}: missing/invalid pair or candidate request outside budget")
    if missing_roles:
        problems.append(f"no successful independent baseline/golden for roles {missing_roles}")
    return dict(passed=not problems, cells=len(rows), gcMode=gc_mode, timedProcesses=len(by_source), expectedProcesses=expected_processes,
                hostGate=plans[0].get("HostGate") if len(plans) == 1 else None,
                runtimePrimers=[dict(path=str(p), scale=v["scale"], sourceSha256=v["sourceSha256"], derivedTables=v.get("derivedTables")) for p, v in primers.items()],
                verifiedManifests=[dict(variant=v, dimension=d, scale=s, sourceSha256=record[2]["sourceSha256"], derivedTables=record[2].get("derivedTables"))
                                   for (v, d, s), record in fixtures.items()],
                candidateWaves=sum(r["candidateSuccessfulSamples"] +
                r["candidateFailedSamples"] for r in rows), candidateRequests=sum(r["candidateRequests"] for r in rows),
                candidateFailedRequests=sum(r["candidateFailedRequests"] for r in rows),
                candidateMaxMs=max((r["candidateMaxMs"] for r in rows if r["candidateMaxMs"] is not None), default=None),
                candidateMinHeadroomMs=min((r["candidateHeadroomMs"] for r in rows if r["candidateHeadroomMs"] is not None), default=None),
                problems=list(dict.fromkeys(problems)))


def quantile(sorted_values, probability):
    position = probability * (len(sorted_values) - 1)
    index = int(position)
    fraction = position - index
    return sorted_values[index] * (1 - fraction) + sorted_values[math.ceil(position)] * fraction


def bootstrap_interval(log_ratios, key):
    salt = hashlib.sha256(json.dumps(key, ensure_ascii=False).encode()).digest()
    generator = random.Random(SEED + int.from_bytes(salt[:8], "big"))
    count = len(log_ratios)
    draws = sorted(median(generator.choices(log_ratios, k=count)) for _ in range(BOOTSTRAPS))
    return math.exp(quantile(draws, 0.025)), math.exp(quantile(draws, 0.975))


def exact_sign_test(log_ratios):
    positive = sum(r > 0 for r in log_ratios)
    negative = sum(r < 0 for r in log_ratios)
    n = positive + negative
    tail = sum(math.comb(n, k) for k in range(min(positive, negative) + 1))
    return min(1.0, (2 * tail) / (2 ** n))


def summarize_cell(key, blocks, problems, expected_blocks=None):
    dimension, scenario, scale = key
    result = dict(dimension=dimension, scenario=scenario, scale=scale)
    times = defaultdict(list)
    counts = defaultdict(Counter)
    paired = defaultdict(list)
    resources = defaultdict(list)
    logs = []
    excluded = 0
    failure_outcomes = Counter()
    budgets = {s.get("budgetMs", s.get("timeoutMs")) for arms in blocks.values()
               for samples in arms.values() for s in samples}
    common_budget = len(budgets) == 1 and all(finite_number(v) and v > 0 for v in budgets)
    budget = next(iter(budgets)) if common_budget else None
    setups = {(s.get("baseScenario", scenario), s.get("regime"), s.get("cacheMode"), s.get("concurrency", 1))
              for arms in blocks.values() for samples in arms.values() for s in samples}
    if len(setups) != 1:
        raise ValueError(f"{key}: workload or cache mode differs between samples")
    base, regime, cache_mode, concurrency = setups.pop()
    result.update(baseScenario=base, regime=regime, cacheMode=cache_mode, concurrency=concurrency)
    requests_by_variant = defaultdict(Counter)
    for variant in VARIANTS:
        modes = {s.get("sqliteCacheMode") for arms in blocks.values() for s in arms.get(variant, ())}
        if len(modes) > 1:
            raise ValueError(f"{key}: SQLite cache mode changed within {variant}")
        result[variant + "SqliteCacheMode"] = next(iter(modes), None)
        observations = (s.get("elapsedMs") for arms in blocks.values() for s in arms.get(variant, ()))
        result[f"{variant}MaxMs"] = max((v for v in observations if finite_number(v) and v >= 0), default=None)
    maximum = result["candidateMaxMs"]
    result.update(budgetMs=budget, candidateHeadroomMs=budget - maximum if budget is not None and maximum is not None else None)
    block_ids = set(blocks) if expected_blocks is None else set(range(expected_blocks))
    unexpected = set(blocks) - block_ids
    if unexpected:
        raise ValueError(f"{key}: block IDs outside expected range 0..{expected_blocks-1}: {sorted(unexpected)}")
    for block in sorted(block_ids):
        variants = blocks.get(block, {})
        fingerprints = set()
        issues = []
        call_failures = []
        failed = set()
        block_times = {}
        for variant in VARIANTS:
            trials = variants.get(variant, [])
            if not trials:
                issues.append(f"missing {variant}")
            elif len({s["source"] for s in trials}) > 1:
                issues.append(f"{variant} block appears in multiple process files")
            successful = []
            for sample in trials:
                outcome = status(sample)
                counts[variant][outcome] += 1
                for request in sample.get("requests", (sample,)):
                    requests_by_variant[variant][status(request)] += 1
                if sample.get("resultFingerprint"):
                    fingerprints.add(str(sample["resultFingerprint"]))
                if outcome == "ok":
                    successful.append(sample)
                else:
                    budget = sample.get("budgetMs", sample.get("timeoutMs"))
                    detail = f" ({sample['error']})" if sample.get("error") else ""
                    reason = f"{variant} {outcome}{detail} [budget {budget if budget is not None else 'unreported'} ms]"
                    if outcome in FAILURE_STATUSES:
                        call_failures.append(reason)
                        failed.add(variant)
                    else:
                        issues.append(reason)
            if successful:
                block_times[variant] = median(s["elapsedMs"] for s in successful)
                times[variant].append(block_times[variant])
                for field in ("allocatedBytes", "commands"):
                    values = [s.get(field) for s in successful if finite_number(s.get(field))]
                    if values:
                        resources[(variant, field)].append(median(values))
        if len(fingerprints) > 1:
            issues.append("result fingerprint mismatch")
        if all(v in variants for v in VARIANTS) and len(variants["baseline"]) != len(variants["candidate"]):
            issues.append("unequal repetition counts")
        latency_valid = not issues and not call_failures
        failure_valid = not issues and common_budget
        if failure_valid:
            failure_outcomes[("baseline" in failed, "candidate" in failed)] += 1
        if not latency_valid or not failure_valid:
            reasons = issues + call_failures
            if not common_budget:
                reasons.append("failure comparison requires a common recorded positive budget")
            sources = sorted({s["source"] for samples in variants.values() for s in samples})
            problems.append(dict(dimension=dimension, scenario=scenario, scale=scale, block=block,
                                 reason=" | ".join(dict.fromkeys(reasons)), sources=", ".join(sources),
                                 latencyPairUsable=latency_valid, failurePairUsable=failure_valid))
        if not latency_valid:
            excluded += 1
            continue
        for variant in VARIANTS:
            paired[variant].append(block_times[variant])
        logs.append(math.log(block_times["baseline"] / block_times["candidate"]))

    for variant in VARIANTS:
        requests = requests_by_variant[variant]
        result[f"{variant}Requests"] = sum(requests.values())
        result[f"{variant}FailedRequests"] = sum(requests.values()) - requests["ok"]
        result[f"{variant}SuccessfulSamples"] = counts[variant]["ok"]
        result[f"{variant}FailedSamples"] = sum(counts[variant].values()) - counts[variant]["ok"]
        result[f"{variant}MedianMs"] = median(times[variant]) if times[variant] else None
        result[f"{variant}PairedMedianMs"] = median(paired[variant]) if logs else None
        for field, suffix in (("allocatedBytes", "AllocatedBytes"), ("commands", "Commands")):
            values = resources[(variant, field)]
            result[f"{variant}Median{suffix}"] = median(values) if values else None
    result.update(blocks=len(block_ids), expectedBlocks=expected_blocks, pairedBlocks=len(logs), excludedBlocks=excluded,
                  speedup=None, ciLow=None, ciHigh=None, signP=None, holmP=None)
    if logs:
        result["speedup"] = math.exp(median(logs))
    if len(logs) >= MIN_BLOCKS:
        result["ciLow"], result["ciHigh"] = bootstrap_interval(logs, key)
        result["signP"] = exact_sign_test(logs)
    improved = failure_outcomes[(True, False)]
    regressed = failure_outcomes[(False, True)]
    both_failed = failure_outcomes[(True, True)]
    observed = sum(failure_outcomes.values())
    result.update(failurePairedBlocks=observed, baselineFailedBlocks=improved + both_failed,
                  candidateFailedBlocks=regressed + both_failed, failureImprovedBlocks=improved,
                  failureRegressedBlocks=regressed, failureP=None, failureHolmP=None)
    if observed == len(block_ids) and observed >= MIN_BLOCKS:
        result["failureP"] = exact_sign_test([1] * improved + [-1] * regressed)
    return result


def classify(rows):
    # Count every matched cell in the family. Insufficient/compromised cells cannot reject.
    family = [r for r in rows if r["expectedBlocks"] is not None or r["pairedBlocks"] or
              (r["baselineSuccessfulSamples"] + r["baselineFailedSamples"] and
               r["candidateSuccessfulSamples"] + r["candidateFailedSamples"])]
    hypotheses = []
    for row in family:
        latency_p = None if row["excludedBlocks"] else row["signP"]
        hypotheses.extend([(latency_p, row, "holmP"), (row["failureP"], row, "failureHolmP")])
    running = 0.0
    for index, (p, row, field) in enumerate(sorted(hypotheses, key=lambda h: h[0] if h[0] is not None else 1.0)):
        running = max(running, min(1.0, (p if p is not None else 1.0) * (len(hypotheses) - index)))
        if p is not None:
            row[field] = running
    for row in rows:
        present = [v for v in VARIANTS if row[f"{v}SuccessfulSamples"] + row[f"{v}FailedSamples"]]
        if len(present) == 1:
            row["conclusion"] = f"{present[0]} only (exploratory)"
        elif row["excludedBlocks"]:
            row["conclusion"] = "incomplete comparison"
        elif row["pairedBlocks"] < MIN_BLOCKS:
            row["conclusion"] = "exploratory"
        elif row["holmP"] < 0.05 and row["ciLow"] > 1:
            row["conclusion"] = "faster"
        elif row["holmP"] < 0.05 and row["ciHigh"] < 1:
            row["conclusion"] = "slower"
        else:
            row["conclusion"] = "inconclusive"
        if len(present) == 1:
            row["failureConclusion"] = f"{present[0]} only (exploratory)"
        elif row["failurePairedBlocks"] != row["blocks"]:
            row["failureConclusion"] = "incomplete comparison"
        elif row["failurePairedBlocks"] < MIN_BLOCKS:
            row["failureConclusion"] = "exploratory"
        elif row["failureHolmP"] < 0.05:
            row["failureConclusion"] = ("fewer failures" if row["failureImprovedBlocks"] > row["failureRegressedBlocks"]
                                        else "more failures")
        else:
            row["failureConclusion"] = "inconclusive"
    return len(hypotheses)


def write_csv(path, rows, fields):
    with path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def display(value):
    return "—" if value is None else f"{value:.4g}"


def figure(rows):
    points = [r for r in rows if r["speedup"] is not None]
    width, left, right = 1150, 555, 1020
    height = 105 + 24 * len(points)
    bottom = height - 46
    extents = [math.log2(v) for r in points for v in (r["ciLow"], r["speedup"], r["ciHigh"]) if v]
    lo, hi = min(-1, math.floor(min(extents, default=0))), max(1, math.ceil(max(extents, default=0)))
    def coord(v):
        return left + (math.log2(v) - lo) / (hi - lo) * (right - left)
    svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" role="img" aria-labelledby="title desc">',
           '<title id="title">Baseline time divided by candidate time</title>',
           '<desc id="desc">Paired median speed ratios with pointwise bootstrap intervals when available. Larger values indicate a faster candidate.</desc>',
           '<rect width="100%" height="100%" fill="white"/><g font-family="Arial, sans-serif" font-size="13" fill="#182832">',
           '<text x="12" y="22" font-weight="bold">Paired speed ratio · baseline time / candidate time</text>']
    ticks = sorted({0, hi, *range(lo, hi + 1, max(1, math.ceil((hi - lo) / 9)))})
    for k in ticks:
        x = coord(2 ** k)
        stroke = "#556a78" if k == 0 else "#dce3e6"
        svg.append(f'<path d="M{x:.2f} 34 V{bottom}" stroke="{stroke}"/><text x="{x:.2f}" y="{bottom+18}" text-anchor="middle">{2 ** k:.3g}×</text>')
    for i, row in enumerate(points):
        y = 50 + i * 24
        label = html.escape(f"{row['dimension']} / {row['scenario']} / {row['scale']}×")
        color = {"faster": "#16714b", "slower": "#a83028"}.get(row["conclusion"], "#50677a")
        svg.append(f'<text x="12" y="{y+4}">{label}</text>')
        if row["ciLow"] is not None:
            svg.append(f'<path d="M{coord(row["ciLow"]):.2f} {y} H{coord(row["ciHigh"]):.2f}" stroke="{color}" stroke-width="3"/>')
        svg.append(f'<circle cx="{coord(row["speedup"]):.2f}" cy="{y}" r="4" fill="{color}"/><text x="{right+15}" y="{y+4}">{display(row["speedup"])}×</text>')
    svg.append('</g></svg>')
    return "".join(svg)


def scale_figure(rows):
    """Print comparable absolute times, including profile-only observations."""
    entries = {(r["dimension"], r["scenario"], r["scale"]): r for r in rows}
    labels = sorted({(r["dimension"], r["scenario"]) for r in rows})
    columns = [(scale, v) for scale in sorted({r["scale"] for r in rows}) for v in VARIANTS
               if any(r["scale"] == scale and r[f"{v}SuccessfulSamples"] + r[f"{v}FailedSamples"] for r in rows)]
    values = [math.log(r[f"{v}MedianMs"]) for r in rows for v in VARIANTS if r[f"{v}MedianMs"]]
    lo, hi = (min(values), max(values)) if values else (0, 1)
    left, cell_width = 390, 110
    width, height = left + cell_width * len(columns) + 12, 100 + len(labels) * 25
    measure = "Median slowest request in successful waves" if any(r["regime"] for r in rows) else "Median successful call time"
    svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" role="img" aria-labelledby="latency-title latency-desc">',
           f'<title id="latency-title">{measure} by workload and scale</title>',
           f'<desc id="latency-desc">{measure} in milliseconds by variant and scale. Darker cells indicate longer elapsed times on a shared log scale. Asterisks indicate failed observations.</desc>',
           '<rect width="100%" height="100%" fill="white"/><g font-family="Arial, sans-serif" font-size="13" fill="#182832">',
           f'<text x="12" y="20" font-weight="bold">{measure} (ms)</text>',
           '<text x="12" y="40">Darker cells took longer. * Failures or budget breaches; see failure counts in the table.</text>']
    for i, (scale, variant) in enumerate(columns):
        x = left + (i + 0.5) * cell_width
        svg.append(f'<text x="{x}" y="64" text-anchor="middle">{variant} {scale}×</text>')
    for index, (dimension, scenario) in enumerate(labels):
        y = 72 + index * 25
        svg.append(f'<text x="12" y="{y+16}">{html.escape(dimension)} / {html.escape(scenario)}</text>')
        for i, (scale, variant) in enumerate(columns):
            row = entries.get((dimension, scenario, scale), {})
            value = row.get(f"{variant}MedianMs")
            alpha = (math.log(value) - lo) / (hi - lo) if value and hi > lo else 0
            shade = tuple(round(a + alpha * (b - a)) for a, b in zip((236, 245, 251), (21, 72, 105)))
            fill = ('#%02x%02x%02x' % shade) if value else '#f0f1f2'
            color = "white" if alpha >= 0.64 else "#182832"
            x = left + i * cell_width
            label = display(value) + ("*" if row.get(f"{variant}FailedSamples") else "")
            svg.append(f'<rect x="{x}" y="{y}" width="{cell_width-2}" height="23" fill="{fill}"/><text x="{x+cell_width/2}" y="{y+16}" text-anchor="middle" fill="{color}">{label}</text>')
    return "".join(svg) + "</g></svg>"


def markdown_cell(value):
    return str(value).replace("|", "&#124;").replace("\n", " ")


def write_reports(output, rows, problems, sources, diagnostics, family_size, failure_endpoint="posthoc", gate=None):
    output.mkdir(parents=True, exist_ok=True)
    write_csv(output / "summary.csv", rows, rows[0].keys())
    write_csv(output / "excluded-blocks.csv", problems, ("dimension", "scenario", "scale", "block", "reason", "sources",
                                                          "latencyPairUsable", "failurePairUsable"))
    (output / "provenance.json").write_text(json.dumps(sources, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    plan_data = {name: {"metadata": sources[name], "diagnosticSamples": samples}
                 for name, samples in diagnostics.items()}
    plan_json = json.dumps(plan_data, indent=2, ensure_ascii=False)
    (output / "query-plans.json").write_text(plan_json + "\n", encoding="utf-8")
    svg = figure(rows)
    (output / "speed-ratios.svg").write_text(svg, encoding="utf-8")
    latencies = scale_figure(rows)
    (output / "latency-by-scale.svg").write_text(latencies, encoding="utf-8")
    headers = ["Dimension", "Operation", "Scale", "Paired / total blocks", "Baseline ms", "Candidate ms",
               "Baseline max ms", "Candidate max ms", "Budget margin ms", "Paired ratio", "95% interval", "Latency Holm p", "Latency result", "Failure pairs",
               "Failed baseline / candidate", "Improved / regressed", "Failure Holm p", "Failure result"]
    matrix = []
    pressure = any(r["regime"] for r in rows)
    if pressure:
        headers.extend(["Clients per wave", "Baseline requests (failed / total)", "Candidate requests (failed / total)"])
    for row in rows:
        interval = "—" if row["ciLow"] is None else f'{display(row["ciLow"])}–{display(row["ciHigh"])}'
        matrix.append([row["dimension"], row["scenario"], f'{row["scale"]}×', f'{row["pairedBlocks"]}/{row["blocks"]}',
                       display(row["baselineMedianMs"]), display(row["candidateMedianMs"]), display(row["baselineMaxMs"]), display(row["candidateMaxMs"]),
                       display(row["candidateHeadroomMs"]), display(row["speedup"]),
                       interval, display(row["holmP"]), row["conclusion"], str(row["failurePairedBlocks"]),
                       f'{row["baselineFailedBlocks"]} / {row["candidateFailedBlocks"]}',
                       f'{row["failureImprovedBlocks"]} / {row["failureRegressedBlocks"]}',
                       display(row["failureHolmP"]), row["failureConclusion"]])
        if pressure:
            matrix[-1].extend([str(row["concurrency"]), f'{row["baselineFailedRequests"]} / {row["baselineRequests"]}',
                               f'{row["candidateFailedRequests"]} / {row["candidateRequests"]}'])
    count = Counter(row["conclusion"] for row in rows)
    overview = f"{len(rows)} workload cells across {len(sources)} input files. Latency outcomes were " + ", ".join(f"{n} {label}" for label, n in sorted(count.items())) + "."
    reliability = Counter(row["failureConclusion"] for row in rows)
    overview += " Failure outcomes were " + ", ".join(f"{n} {label}" for label, n in sorted(reliability.items())) + "."
    if gate is not None:
        outcome = "passed" if gate["passed"] else "failed"
        overview += f" The strict candidate budget gate {outcome} across {gate['candidateRequests']} observed requests."
        (output / "candidate-budget-gate.json").write_text(json.dumps(gate, indent=2) + "\n", encoding="utf-8")
    endpoint_design = ("The paired failure endpoint was prespecified before this comparison."
                       if failure_endpoint == "prespecified" else
                       "The supplementary, post hoc failure endpoint was specified during collection after exploratory profiling showed timeouts.")
    method = [
        "Each block represents a paired process trial. Timings are warm-cache observations on one machine. Independent process starts do not establish performance on other machines or under cold-disk or concurrent-writer conditions.",
        "The baseline and candidate columns show medians of successful within-block median times for each variant. The paired ratio is the exponential of the median within-block log ratio (baseline time divided by candidate time). Values above one favor the candidate. The CSV also includes medians on the paired subset.",
        f"Latency is the primary endpoint. Pointwise 95% percentile intervals resample complete paired log ratios {BOOTSTRAPS:,} times with fixed seed {SEED}. The exact two-sided paired sign test discards ties. A directional latency finding requires adjusted p < 0.05 and an interval entirely on one side of one.",
        "Maximum times include all observed elapsed times, including time spent before a timeout or error. Budget margin is the common service budget minus the candidate maximum. The strict observed target requires every candidate call to succeed below the budget. Future calls can take longer.",
        endpoint_design + " A block fails for a variant if any repetition times out, exceeds the service budget, or returns an error. The exact two-sided paired McNemar test compares discordant blocks, conditional on their total. Blocks in which both variants fail contribute to both failure counts. Fewer or more failures requires adjusted p < 0.05.",
        f"Each endpoint requires at least {MIN_BLOCKS} complete process-block pairs and no missing pairs for that endpoint. A single Holm step-down adjustment covers {family_size} hypotheses, comprising latency and failure for every workload cell in the comparison family. Untestable hypotheses count as p = 1.",
        "Timeouts and errors exclude a block from latency ratios. Recorded failures still enter the paired failure comparison when variants used the same recorded positive budget and have equal repetition counts. Missing variants, duplicate process files, invalid successful results, or differing available fingerprints disqualify both comparisons. Failure counts use eligible failure pairs. Individual reasons and budgets are in excluded-blocks.csv. Input metadata is in provenance.json.",
    ]
    if pressure:
        method[0] = ("Each process block supplies one wave per workload cell. Its latency is the maximum individual request time from the common arrival, including queueing. "
                     "The solo condition has one client; bursts have four simultaneous clients. Individual requests supply descriptive counts. "
                     "Sign tests and bootstrap resampling operate on paired process blocks. A wave fails if any client fails.")
        for record in sources.values():
            plan = record.get("campaignPlan", {})
            common = plan.get("SqliteCacheMode", plan.get("CacheMode"))
            before, after = (plan.get(v + "SqliteCacheMode", common) for v in ("Baseline", "Candidate"))
            if before is not None and after is not None:
                method.append(f"The baseline uses SQLite Cache={before}; the candidate uses Cache={after}. " +
                              ("This comparison includes the declared connection configuration change alongside query and schema changes; it does not isolate their individual effects."
                               if before != after else "Both variants use the same declared SQLite connection cache mode."))
                role_limits = {variant: plan[variant + "MmapLimitsByRole"] for variant in ("Baseline", "Candidate")
                               if plan.get(variant + "MmapLimitsByRole")}
                if role_limits:
                    method.append("Declared per-role SQLite mmap limits (bytes) are " + json.dumps(role_limits, sort_keys=True) +
                                  ". The gate checks each request's role and actual observed limit; other roles use the declared per-variant or common limit. Limits describe permitted mapping size, not resident memory.")
                break
        warm_kinds = {check.get("targetKind") for record in sources.values()
                      for check in record.get("metadata", {}).get("copyChecks", ()) if check.get("regime") == "warm-burst"}
        warm_method = ("Warm cells reuse the validated synthetic source after target warmups."
                       if warm_kinds == {"validated-warm-source"} else "Warm cells receive target warmups.")
        method.append("Cold cells use first target access on a new unbuffered-copy database after warming the runtime on a separate fixture. "
                      + warm_method + " Post-wave hashes check the measured target. Device and background caches are uncontrolled. Results describe isolated bursts on this machine.")
        if gate and (host_policy := gate.get("hostGate")):
            method.append(f"Before each measured arrival, both variants wait for {host_policy['consecutiveSamples']} consecutive "
                          f"{host_policy['pollMs']} ms samples with current disk queue at most {host_policy['maxCurrentDiskQueue']}, "
                          f"average disk queue at most {host_policy['maxAverageDiskQueue']}, and host CPU at most "
                          f"{host_policy['maxCpuPercent']}%. The host wait is setup time. Every request outcome after arrival "
                          "remains in the comparison. The results are conditional on this observed pre-arrival host state.")
        if any(record.get("campaignPlan", {}).get("RuntimeMetadataFiles") for record in sources.values()):
            method.append("The declared runtime priming uses independent, remapped 1x and 100x history fixtures. Each workload receives two four-client waves per primer with a 60000 ms warmup budget before measured arrival. "
                          "The strict gate verifies manifest identity, fixture independence, complete ordered priming events and timestamps. This measures a warm runtime with first access to the measured database; it does not measure cold application startup.")
            for record in sources.values():
                prime_mode = record.get("campaignPlan", {}).get("RuntimePrimeSqliteCacheMode")
                if prime_mode is not None:
                    method.append(f"Runtime priming uses SQLite Cache={prime_mode} in both arms. Timed requests use each variant's separately declared connection mode.")
                    break
        if any("ColdCopyWorkers" in record.get("campaignPlan", {}) for record in sources.values()):
            method.append("All cold replicas are copied before the declared settling interval and measured collection. Integrity hashes are read after every measured wave has finished. The strict gate verifies copy counts, worker limits, space requirements and timestamp ordering with a 20 ms tolerance for UTC clock granularity.")
        if any("ScenarioBatches" in record.get("campaignPlan", {}) for record in sources.values()):
            method.append("The ordered scenario batches run in separate processes to bound staged-copy volume. Each cell still contributes one wave per arm per block; batches and four clients do not increase its paired sample size. Completeness is checked against the full campaign matrix and every declared process batch.")
    if rows[0]["expectedBlocks"] is not None:
        method.append(f"The campaign requires blocks 0 through {rows[0]['expectedBlocks']-1} for every observed workload cell. Missing blocks enter the exclusion table even when both variants are absent. A cell that produced no samples in either variant must be checked against the campaign's scenario inventory.")
        if rows[0]["expectedBlocks"] == 16:
            method.append(f"Sixteen independent randomized baseline/candidate process pairs supply the maximum sample size per cell; four clients do not increase it. With sixteen unanimous non-tied pairs the minimum exact two-sided p is 2/2^16. "
                          f"The first Holm threshold is 0.05/{family_size}; exploratory runs outside the declared comparison are not pooled into these pairs.")
    figures_md = ["![Median latency by scale](latency-by-scale.svg)"]
    if any(row["pairedBlocks"] for row in rows):
        figures_md.extend(["", "![Paired speed ratios](speed-ratios.svg)"])
    md = ["# Database load measurements", "", overview, "", *figures_md, "",
          "| " + " | ".join(headers) + " |", "| " + " | ".join("---" for _ in headers) + " |"]
    md.extend("| " + " | ".join(map(markdown_cell, line)) + " |" for line in matrix)
    md.extend(["", "## Measurement method", "", "\n\n".join(method)])
    if diagnostics:
        md.extend(["", f"See the [diagnostic samples and query plans](query-plans.json) from {len(diagnostics)} instrumented process runs. These runs are excluded from the timings."])
    problem_counts = Counter(p["reason"] for p in problems)
    if problem_counts:
        md.extend(["", "## Excluded blocks", ""])
        md.extend(f"- {count} block(s) — {markdown_cell(reason)}" for reason, count in problem_counts.items())
    (output / "report.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    page = ['<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Database load measurements</title>',
            '<style>body{font:16px system-ui;color:#182832;max-width:1240px;margin:2rem auto;padding:0 1rem}section{overflow:auto}p{max-width:85ch}table{border-collapse:collapse;font-size:14px}th,td{padding:.45rem .6rem;border-bottom:1px solid #d6dfe3;text-align:left}th{background:#edf2f5}</style>',
            '<main><h1>Database load measurements</h1>', f'<p>{html.escape(overview)}</p>', '<section>', latencies, '</section>']
    if any(row["pairedBlocks"] for row in rows):
        page.extend(['<section>', svg, '</section>'])
    page.append('<section><table><thead><tr>')
    page.extend(f'<th scope="col">{h}</th>' for h in headers)
    page.append('</tr></thead><tbody>')
    page.extend('<tr>' + ''.join(f'<td>{html.escape(str(v))}</td>' for v in line) + '</tr>' for line in matrix)
    page.append('</tbody></table></section><h2>Measurement method</h2>')
    page.extend(f'<p>{html.escape(paragraph)}</p>' for paragraph in method)
    if diagnostics:
        page.append(f'<details><summary>Diagnostic samples and query plans ({len(diagnostics)} files)</summary><pre>{html.escape(plan_json)}</pre></details>')
    if problem_counts:
        page.append('<h2>Excluded blocks</h2><ul>')
        page.extend(f'<li>{count} block(s) — {html.escape(reason)}</li>' for reason, count in problem_counts.items())
        page.append('</ul>')
    page.append('</main></html>')
    (output / "report.html").write_text("\n".join(page), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    inputs = parser.add_mutually_exclusive_group(required=True)
    inputs.add_argument("--input", type=Path, help="Directory of raw process-run JSON files")
    inputs.add_argument("--preflight-plan", type=Path, help="Check the planned pressure fixture and primer manifests before collection")
    parser.add_argument("--output", type=Path, help="Report directory")
    parser.add_argument("--expected-blocks", type=int, help="Require every block ID from 0 to N-1 in each observed cell")
    parser.add_argument("--require-candidate-budget", action="store_true",
                        help="Write gate evidence and exit nonzero unless the complete planned candidate workload meets its budget")
    parser.add_argument("--goldens", type=Path, action="append", default=[], metavar="PATH",
                        help="Baseline raw samples directory or JSON list of dimension/scale/scenario/resultFingerprint records; repeatable")
    parser.add_argument("--failure-endpoint", choices=("posthoc", "prespecified"), default="posthoc",
                        help="Label when the paired failure endpoint was specified (default: posthoc)")
    args = parser.parse_args()
    if args.preflight_plan is not None:
        plan = json.loads(args.preflight_plan.read_text(encoding="utf-8-sig"))
        problems = []
        fixtures = planned_pressure_fixtures(plan, problems)
        primers = planned_primers(plan, problems)
        if problems:
            raise SystemExit("Pressure manifest preflight failed:\n" + "\n".join(problems))
        print(f"Pressure preflight verified {len(fixtures)} measured and {len(primers)} runtime manifests.")
        return
    if args.output is None:
        parser.error("--input requires --output")
    if args.expected_blocks is not None and args.expected_blocks < 1:
        parser.error("--expected-blocks must be positive")
    groups, sources, diagnostics = read_trials(args.input)
    problems = []
    rows = [summarize_cell(key, groups[key], problems, args.expected_blocks) for key in sorted(groups)]
    family_size = classify(rows)
    missing = (role_fingerprints(groups, args.goldens)
               if args.require_candidate_budget or args.goldens or any(r["regime"] for r in rows) else [])
    gate = candidate_budget_gate(rows, groups, sources, args.expected_blocks, missing) if args.require_candidate_budget else None
    if gate is not None:
        gate["goldenSources"] = [str(p) for p in args.goldens]
    write_reports(args.output, rows, problems, sources, diagnostics, family_size, args.failure_endpoint, gate)
    print(f"{len(rows)} cells, {len(problems)} excluded blocks. Report: {args.output / 'report.html'}")
    if gate is not None and not gate["passed"]:
        raise SystemExit(f"Candidate budget gate failed. See {args.output / 'candidate-budget-gate.json'}")


if __name__ == "__main__":
    main()
