"""Independently reconcile a completed pressure campaign's raw JSON and summary CSV.

No analyzer imports, database access, benchmark calls or bootstrap work. Recomputes
paired signs, discordant failures, joint Holm adjustment, counts and observed maxima.
Bootstrap intervals and manifest integrity remain separate analyzer-gate evidence.
"""

import argparse
from collections import defaultdict
import csv
from datetime import datetime
import hashlib
import json
import math
from pathlib import Path
import statistics


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sign_probability(positive, negative):
    total = positive + negative
    return min(1.0, 2 * sum(math.comb(total, i) for i in range(min(positive, negative) + 1)) / 2**total)


def verify_derived(fixture, variant, plan):
    declarations = {"DerivedSummaryVersion": ("activityRecordEffortSummaries", "ActivityStreams", "sourceStreamCount"),
                    "ExplorationCellSetVersion": ("activityExplorationCellSets", "ActivityExplorationIndexes", "sourceIndexCount"),
                    "ExplorationAggregateVersion": ("activityExplorationAggregateRows", "Activities", "sourceActivityCount"),
                    "PeriodRecordRowVersion": ("activityPeriodRecordRows", "Activities", "sourceActivityCount")}
    tables = fixture.get("derivedTables") or {}
    expected = {values[0] for field, values in declarations.items() if plan.get(variant.title() + field) is not None}
    require(set(tables) == expected, f"{variant}: derived table inventory differs from plan")
    for field, (table, source, count_field) in declarations.items():
        version = plan.get(variant.title() + field)
        if version is None:
            continue
        require(type(version) is int and version > 0, f"{variant}: invalid planned derived version")
        data = tables[table]
        count = fixture.get("counts", {}).get(source)
        require(type(count) is int and count >= 0 and isinstance(data, dict)
                and all(type(data.get(k)) is int for k in ("rowCount", count_field, "invalidRows", "computationVersion"))
                and data["rowCount"] == data[count_field] == count
                and data["invalidRows"] == 0 and data["computationVersion"] == version,
                f"{variant}: derived integrity mismatch for {table}")


def verify_mmap(request, variant, plan):
    prefix = variant.title()
    default = plan.get(prefix + "MmapProductionLimit", plan.get("MmapProductionLimit"))
    mapping = plan.get(prefix + "MmapLimitsByRole", {})
    require(default is None or type(default) is int and default >= 0, "Invalid default mmap limit")
    require(isinstance(mapping, dict) and all(isinstance(k, str) and k and type(v) is int and v >= 0
                                             for k, v in mapping.items()), "Invalid role mmap limits")
    expected = mapping.get(request["scenario"], default)
    observed = request.get("effectiveMmapLimitBytes")
    require(isinstance(observed, list) and all(type(v) is int and v >= 0 for v in observed)
            and (request["status"] != "ok" or bool(observed)), "Missing or invalid actual mmap observations")
    require(expected is None or all(v == expected for v in observed), f"Actual mmap limit mismatch for {variant}/{request['scenario']}")


def verify_host_readiness(samples, metadata, plan):
    """Verify release on the first eligible counter window, independently of call outcomes."""
    policy = plan.get("HostGate")
    evidence = metadata.get("hostReadiness", [])
    require(metadata.get("config", {}).get("hostGate") == policy, "Host readiness policy differs from plan")
    if policy is None:
        require(not evidence, "Unplanned host readiness gating")
        return []
    limits = {"currentDiskQueue": "maxCurrentDiskQueue", "averageDiskQueue": "maxAverageDiskQueue", "cpuPercent": "maxCpuPercent"}
    fields = set(limits.values()) | {"pollMs", "consecutiveSamples", "maxWaitMs"}
    def nonnegative(value):
        return type(value) in (int, float) and math.isfinite(value) and value >= 0
    require(isinstance(policy, dict) and set(policy) == fields
            and all(nonnegative(policy[k]) for k in fields)
            and all(type(policy[k]) is int for k in ("pollMs", "consecutiveSamples", "maxWaitMs"))
            and policy["pollMs"] >= 250 and policy["consecutiveSamples"] >= 2
            and policy["maxCpuPercent"] <= 100
            and policy["maxWaitMs"] > policy["pollMs"] * policy["consecutiveSamples"], "Invalid host readiness policy")
    waves = {(s["baseScenario"], s["regime"]): s for s in samples}
    require(len(waves) == len(samples) and isinstance(evidence, list) and len(evidence) == len(samples), "Host readiness workload count mismatch")
    def timestamp(text):
        stamp = datetime.fromisoformat(text.replace("Z", "+00:00"))
        require(stamp.utcoffset() is not None, "Host readiness timestamp lacks time zone")
        return stamp
    seen, waits = set(), []
    for record in evidence:
        key = record["scenario"], record["regime"]
        require(key in waves and key not in seen and record.get("passed") is True, "Duplicate, unknown or rejected host readiness window")
        seen.add(key)
        start, end = timestamp(record["startedUtc"]), timestamp(record["completedUtc"])
        arrival = timestamp(waves[key]["arrivalUtc"])
        elapsed = (end - start).total_seconds() * 1000
        require(start <= end <= arrival and elapsed <= policy["maxWaitMs"] + 50, "Host readiness violates pre-arrival wait bounds")
        primes = [timestamp(p["completedUtc"]) for p in metadata.get("priming", ())
                  if (p["scenario"], p["regime"]) == key]
        require(not primes or max(primes) <= start, "Host readiness precedes completion of priming")
        observations = record.get("observations")
        require(isinstance(observations, list) and observations, "Missing host readiness observations")
        previous, streak = start, 0
        for index, observation in enumerate(observations):
            stamp = timestamp(observation["utc"])
            require((stamp - previous).total_seconds() * 1000 >= policy["pollMs"] - 50 and stamp <= end,
                    "Host readiness counter timestamps violate polling order")
            previous = stamp
            error = observation.get("errorCode")
            if error is not None:
                require(isinstance(error, str) and error in {"0x800007D4", "0x800007D5", "0x800007D6",
                        "0x800007D7", "0x800007D8", "0xC0000BBA"}
                        and all(k in observation and observation[k] is None for k in limits),
                        "Invalid rejected host readiness counter observation")
                streak = 0
                continue
            require(all(nonnegative(observation.get(k)) for k in limits) and observation["cpuPercent"] <= 100,
                    "Invalid host readiness counter values")
            quiet = all(observation[k] <= policy[maximum] for k, maximum in limits.items())
            streak = streak + 1 if quiet else 0
            require(streak != policy["consecutiveSamples"] or index == len(observations) - 1,
                    "Host readiness continued after its first passing window")
        require(streak == policy["consecutiveSamples"] and (arrival - previous).total_seconds() <= 1,
                "Host readiness lacks a fresh passing window")
        waits.append((waves[key]["variant"], elapsed))
    return waits


def verify_cold_stage(samples, metadata, plan):
    staged = "ColdCopyWorkers" in plan or "ColdCopySettleMs" in plan
    if not staged and "ScenarioBatches" not in plan:
        return
    scenarios = plan["Scenarios"]
    if "ScenarioBatches" in plan:
        batches = plan["ScenarioBatches"]
        require(isinstance(batches, list) and batches and all(isinstance(b, list) and b for b in batches)
                and [s for b in batches for s in b] == scenarios and len(set(scenarios)) == len(scenarios),
                "Scenario batches do not flatten to the unique ordered plan")
        scenarios = metadata.get("config", {}).get("scenarios")
        require(scenarios in batches, "Process scenarios do not match an allowed ordered batch")
    cases = {(s, r) for s in scenarios for r in plan["Regimes"] if (s, r) != ("mixed", "cold-solo")}
    require(len(samples) == len(cases) and {(s["baseScenario"], s["regime"]) for s in samples} == cases,
            "Staged process workload inventory mismatch")
    if not staged:
        return
    n = sum(regime != "warm-burst" for _, regime in cases)
    stage = metadata.get("coldPreparation", {})
    workers, delay = plan.get("ColdCopyWorkers"), plan.get("ColdCopySettleMs")
    size = metadata.get("fixture", {}).get("databaseBytes")
    require(type(workers) is int and workers > 0 and type(delay) is int and delay >= 0
            and type(size) is int and size > 0, "Invalid planned cold staging limits or fixture size")
    require(all(type(stage.get(k)) is int for k in ("replicaCount", "copyConcurrency", "requiredBytes", "freeBytes", "settleMs"))
            and stage["replicaCount"] == n and stage["copyConcurrency"] == min(workers, n)
            and stage["requiredBytes"] == size * n and stage["freeBytes"] >= size * n
            and stage["settleMs"] == (delay if n else 0), "Cold staging count, workers, bytes or delay mismatch")
    def seconds(value):
        date = datetime.fromisoformat(value.replace("Z", "+00:00"))
        require(date.utcoffset() is not None, "Cold staging timestamps require time zones")
        return date.timestamp()
    arrivals = [seconds(s["arrivalUtc"]) for s in samples]
    last_finish = max(start + s["elapsedMs"] / 1000 for start, s in zip(arrivals, samples))
    if n:
        copied, ready = seconds(stage["copiesReadyUtc"]), seconds(stage["collectionReadyUtc"])
        require(ready - copied >= delay / 1000 - 0.020 and ready <= min(arrivals) + 0.020,
                "Cold staging timestamps violate settling or collection order")
    checks = metadata.get("copyChecks", ())
    require(len(checks) == len(cases) and {(c["scenario"], c["regime"]) for c in checks} == cases,
            "Missing staged-copy integrity evidence")
    require(all(seconds(c["verifiedUtc"]) >= last_finish - 0.020 for c in checks),
            "Integrity reads preceded completion of measured waves")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--goldens", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    plan_bytes = (args.runs / "campaign-plan.json").read_bytes()
    plan = json.loads(plan_bytes.decode("utf-8-sig"))
    for path in plan.get("RuntimeMetadataFiles", ()):
        verify_derived(json.loads(Path(path).read_text(encoding="utf-8-sig")), "candidate", plan)
    golden_bytes = args.goldens.read_bytes()
    goldens = {(r["dimension"], r["scale"], r["scenario"]): r["resultFingerprint"]
               for r in json.loads(golden_bytes.decode("utf-8-sig"))}
    expected = {(d, f"{s}.{r}", scale) for d in plan["Dimensions"] for scale in plan["Scales"]
                for s in plan["Scenarios"] for r in plan["Regimes"] if (s, r) != ("mixed", "cold-solo")}
    groups, hashes, diagnostics = defaultdict(dict), {}, []
    setup_waits = defaultdict(list)
    for path in sorted(args.runs.rglob("samples.json")):
        raw = path.read_bytes()
        data = json.loads(raw.decode("utf-8-sig"))
        metadata = data["metadata"]
        timed = [s for s in data.get("samples", ()) if s.get("variant") in ("baseline", "candidate")]
        if timed:
            verify_cold_stage(timed, metadata, plan)
            for variant, elapsed in verify_host_readiness(timed, metadata, plan):
                setup_waits[variant].append(elapsed)
        for s in data.get("samples", ()):
            variant = s["variant"]
            if variant.startswith("diagnostic-"):
                diagnostics.append(str(path.relative_to(args.runs)))
                continue
            require(variant in ("baseline", "candidate"), f"Unexpected variant {variant}")
            verify_derived(metadata.get("fixture", {}), variant, plan)
            require(metadata.get("config", {}).get("diagnostic", False) is False
                    and s.get("diagnostics") is None
                    and all(r.get("diagnostics") is None for r in s.get("requests", ())),
                    f"Diagnostic instrumentation present in formal comparison {path}")
            key = s["dimension"], s["scenario"], s["scale"]
            block_key = s["block"], variant
            require(block_key not in groups[key], f"Duplicate block/arm {key} {block_key}")
            require(metadata["driverHash"] == plan["DriverHash"], f"Driver hash mismatch {path}")
            require(metadata["productionHash"] == plan[variant.title() + "AssemblyHash"], f"Production hash mismatch {path}")
            require(metadata["serverGc"] == (plan["GcMode"].lower() == "server"), f"GC mismatch {path}")
            expected_mode = plan.get(variant.title() + "SqliteCacheMode", plan.get("SqliteCacheMode"))
            require(expected_mode in ("Shared", "Private", "Default")
                    and metadata.get("sqliteCacheMode") == expected_mode
                    and s.get("sqliteCacheMode") == expected_mode
                    and metadata["config"].get("cacheMode") == expected_mode, f"SQLite mode mismatch {path}")
            require(s["budgetMs"] == plan["BudgetMs"], f"Budget mismatch {path}")
            for field in ("block", "variant", "dimension", "scale", "budgetMs"):
                require(s[field] == metadata["config"][field], f"Process/sample mismatch {path}/{field}")
            roles = (["records-period-streams", "records-attempts", "records-history", "imports-all"]
                     if s["baseScenario"] == "mixed" else [s["baseScenario"]] * s["concurrency"])
            requests = sorted(s["requests"], key=lambda r: r["slot"])
            require(s["concurrency"] == (1 if s["regime"] == "cold-solo" else 4), f"Concurrency mismatch {path}")
            require([r["slot"] for r in requests] == list(range(len(roles))), f"Slot inventory mismatch {path}")
            require([r["scenario"] for r in requests] == roles, f"Role inventory mismatch {path}")
            require(s["scenario"] == s["baseScenario"] + "." + s["regime"], f"Scenario mismatch {path}")
            for r in requests:
                verify_mmap(r, variant, plan)
                require(math.isfinite(r["elapsedMs"]) and 0 <= r["enteredMs"] <= r["elapsedMs"], f"Invalid timing {path}")
                if r["status"] == "ok":
                    require(r["elapsedMs"] < s["budgetMs"], f"Masked request budget failure {path}")
                    require(r["resultFingerprint"] == goldens[s["dimension"], s["scale"], r["scenario"]], f"Golden mismatch {path}/{r['scenario']}")
            require(s["elapsedMs"] == max(r["elapsedMs"] for r in requests), f"Wave tail mismatch {path}")
            failed = any(r["status"] != "ok" for r in requests)
            require((s["status"] != "ok") == failed, f"Masked wave failure {path}")
            if not failed:
                fingerprint = hashlib.sha256("\n".join(f"{r['slot']}|{r['scenario']}|{r['resultFingerprint']}" for r in requests).encode()).hexdigest().upper()
                require(s["resultFingerprint"] == fingerprint, f"Composite fingerprint mismatch {path}")
            require(variant != "candidate" or not failed, f"Candidate failed {key}/{s['block']}")
            groups[key][block_key] = s
            hashes[str(path.relative_to(args.runs))] = hashlib.sha256(raw).hexdigest()
    require(set(groups) == expected, "Raw workload matrix differs from plan")
    expected_processes = None
    if "ScenarioBatches" in plan:
        expected_processes = 2 * plan["Blocks"] * len(plan["Dimensions"]) * len(plan["Scales"]) * len(plan["ScenarioBatches"])
        require(len(hashes) == expected_processes, "Timed process count differs from declared batch plan")
    with (args.report / "summary.csv").open(newline="", encoding="utf-8-sig") as file:
        records = list(csv.DictReader(file))
    reported = {(r["dimension"], r["scenario"], int(r["scale"])): r for r in records}
    require(len(records) == len(reported) and set(reported) == expected, "Summary workload matrix differs from plan")
    hypotheses, maximum, requests_count = [], 0, 0
    def compare(key, field, value):
        actual = reported[key][field]
        require(actual == "" if value is None else actual != "" and math.isclose(float(actual), value, rel_tol=1e-10, abs_tol=1e-10), f"CSV mismatch {key}/{field}: {actual} versus {value}")
    for key in sorted(expected):
        cells = groups[key]
        require(set(cells) == {(i, v) for i in range(plan["Blocks"]) for v in ("baseline", "candidate")}, f"Incomplete pairs {key}")
        logs, improved, regressed = [], 0, 0
        for i in range(plan["Blocks"]):
            b, c = cells[i, "baseline"], cells[i, "candidate"]
            bf, cf = b["status"] != "ok", c["status"] != "ok"
            improved += bf and not cf
            regressed += cf and not bf
            if not bf and not cf:
                logs.append(math.log(b["elapsedMs"] / c["elapsedMs"]))
        latency_p = sign_probability(sum(x > 0 for x in logs), sum(x < 0 for x in logs)) if len(logs) >= 12 else None
        failure_p = sign_probability(improved, regressed) if plan["Blocks"] >= 12 else None
        values = dict(pairedBlocks=len(logs), excludedBlocks=plan["Blocks"] - len(logs),
                      failurePairedBlocks=plan["Blocks"], failureImprovedBlocks=improved, failureRegressedBlocks=regressed,
                      speedup=math.exp(statistics.median(logs)) if logs else None, signP=latency_p, failureP=failure_p)
        for variant in ("baseline", "candidate"):
            observations = [cells[i, variant] for i in range(plan["Blocks"])]
            values[variant + "MaxMs"] = max(s["elapsedMs"] for s in observations)
            values[variant + "FailedBlocks"] = sum(s["status"] != "ok" for s in observations)
            values[variant + "Requests"] = sum(len(s["requests"]) for s in observations)
            values[variant + "FailedRequests"] = sum(r["status"] != "ok" for s in observations for r in s["requests"])
        values["candidateHeadroomMs"] = plan["BudgetMs"] - values["candidateMaxMs"]
        maximum = max(maximum, values["candidateMaxMs"])
        requests_count += values["candidateRequests"]
        for field, value in values.items():
            compare(key, field, value)
        hypotheses.extend([(latency_p if len(logs) == plan["Blocks"] else None, key, "holmP"), (failure_p, key, "failureHolmP")])
    running = 0
    for index, (p, key, field) in enumerate(sorted(hypotheses, key=lambda h: h[0] if h[0] is not None else 1)):
        running = max(running, min(1, (p if p is not None else 1) * (len(hypotheses) - index)))
        compare(key, field, running if p is not None else None)
    evidence = dict(passed=True, cells=len(expected), hypotheses=len(hypotheses), blocks=plan["Blocks"], seed=plan["Seed"],
                    timedProcesses=len(hashes), expectedProcesses=expected_processes,
                    hostGate=plan.get("HostGate"),
                    hostSetupWaits={variant: dict(count=len(setup_waits[variant]),
                        minMs=min(setup_waits[variant]) if setup_waits[variant] else None,
                        medianMs=statistics.median(setup_waits[variant]) if setup_waits[variant] else None,
                        maxMs=max(setup_waits[variant]) if setup_waits[variant] else None)
                        for variant in ("baseline", "candidate")},
                    candidateRequests=requests_count, candidateMaxMs=maximum, candidateMinHeadroomMs=plan["BudgetMs"] - maximum,
                    planSha256=hashlib.sha256(plan_bytes).hexdigest(), goldensSha256=hashlib.sha256(golden_bytes).hexdigest(),
                    rawFiles=hashes, diagnosticFilesExcluded=sorted(set(diagnostics)),
                    limitations="Bootstrap CIs, manifest integrity and execution-order seed replay are separate checks.")
    args.output.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(f"Independent audit passed: {len(expected)} cells, {requests_count} candidate requests, maximum {maximum:.4f} ms")


if __name__ == "__main__":
    main()
