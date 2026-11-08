"""Rebuild independent pressure-query fingerprints from retained, vetted baseline JSON.

Reads measurement JSON and manifests only; never opens a benchmark database.
"""

import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, default=Path(__file__).resolve().parents[1] / "artifacts")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = args.artifacts.resolve()
    output = args.output or root / "database-pressure-goldens-v1.json"
    expected = {}
    for dimension in ("history", "profiles"):
        for scale in (100, 1000):
            path = root / "database-pressure-metadata-v2" / f"{dimension}-{scale}.json"
            expected[dimension, scale] = json.loads(path.read_text(encoding="utf-8-sig"))["counts"]
    observations = defaultdict(lambda: defaultdict(list))
    selections = [
        # Earlier diagnostic-baseline fixtures had 200 StatisticSnapshots versus the formal two.
        ("database-load-results/runs", {"baseline"}, None),
        ("record-budget-correctness-before", {"baseline", "diagnostic-baseline"},
         {("history", s, r) for s in (100, 1000)
          for r in ("records-attempts", "records-history", "records-period-streams")}),
        ("database-pressure-before-server", {"baseline", "diagnostic-baseline"},
         {("history", 1000, r) for r in ("imports-all", "imports-owner")}),
    ]
    for folder, variants, allowed in selections:
        for path in sorted((root / folder).rglob("samples.json")):
            raw = path.read_bytes()
            document = json.loads(raw.decode("utf-8-sig"))
            metadata = document.get("metadata", {})
            counts = metadata.get("fixture", {}).get("counts", metadata.get("counts"))
            for sample in document.get("samples", ()):
                fixture = sample.get("dimension"), sample.get("scale")
                if sample.get("variant") not in variants or fixture not in expected:
                    continue
                for request in sample.get("requests", (sample,)):
                    key = (*fixture, request["scenario"])
                    if allowed is not None and key not in allowed:
                        continue
                    if request.get("status") != "ok" or not request.get("resultFingerprint"):
                        continue
                    if counts != expected[fixture]:
                        raise ValueError(f"{path}: original table counts differ from measured fixture manifest")
                    evidence = dict(path=str(path.relative_to(root)), sha256=hashlib.sha256(raw).hexdigest(),
                                    variant=sample["variant"], block=sample["block"],
                                    originalTableCounts=counts,
                                    productionModule=metadata.get("productionModule"),
                                    productionHash=metadata.get("productionHash"))
                    if "slot" in request:
                        evidence["slot"] = request["slot"]
                    observations[key][request["resultFingerprint"]].append(evidence)
    if len(observations) != 92 or any(len(values) != 1 for values in observations.values()):
        raise ValueError("Expected exactly 92 independent roles with one consistent fingerprint each")
    roles = [set(k[2] for k in observations if k[:2] == fixture) for fixture in expected]
    if any(len(role) != 23 or role != roles[0] for role in roles):
        raise ValueError("All four fixtures must contain the same 23 query roles")
    rows = []
    for (dimension, scale, scenario), values in sorted(observations.items()):
        fingerprint, sources = next(iter(values.items()))
        rows.append(dict(dimension=dimension, scale=scale, scenario=scenario,
                         resultFingerprint=fingerprint, sources=sources))
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {len(rows)} independent role fingerprints with source hashes to {output}")


if __name__ == "__main__":
    main()
