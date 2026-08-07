# Local segment methodology

A segment is a local, owner-scoped, directional, sport-specific path. It is not connected to Strava's segment catalog.

## Creating a segment

Open an activity detail, enter a name, choose a start and end point from that activity's ordered track, and optionally change the default tolerance. The resulting line stores owner and sport, ordered WKB path and bounding box, path distance, tolerance, and source activity provenance.

The segment is then compared with historical activities for the same owner and sport. New imports recompute existing segments.

## Matching pipeline

The implementation follows these stages:

1. Use bounding boxes expanded by tolerance to remove impossible activities.
2. Reject activities without enough GPS points.
3. Resample the segment and candidate track at approximately 10 metre intervals.
4. Search in the segment's forward direction only.
5. Require start and end proximity, monotonic progress, and cross-track proximity.
6. Require at least 95% segment path coverage.
7. Reject passes containing a GPS timestamp gap over 30 seconds.
8. Continue after a match to retain repeated passes in the same activity.

Default start, end, and cross-track tolerance is 30 metres. A segment stores an advanced per-segment override for noisy environments.

Reverse-direction travel does not match. A nearby parallel road should fail cross-track/path-progress checks even if start and end points are close. Loops depend on ordered monotonic progress, not only geometry intersection.

## Effort metrics

Each accepted pass records elapsed and moving time; ascent, descent, and average grade; average/maximum speed, heart rate, cadence, and power; average temperature and respiration; coverage; start time; stream indices; and the linked activity. Missing sensors remain null.

Per-owner ranks are recalculated from elapsed time. The inline segment explorer shows the directional path and elevation profile, a sortable comparison table, and the selected pass highlighted with start/end points. Selecting a pass updates the `effort` URL query and renders synchronized charts from that exact activity-stream slice; no duplicate effort stream is stored. Pauses remain part of elapsed time, while moving time excludes stationary samples when usable position/timestamps exist.

## Accuracy limitations

This is a transparent local heuristic, not a certified race timing system. Results depend on GPS sampling and accuracy, device smoothing, timestamps, tunnels/tree cover/urban reflections, pauses, and how precisely the path was selected.

Increasing tolerance can recover a noisy match but also increases false positives, especially around parallel paths and switchbacks. Use the smallest value that covers normal GPS variation and inspect linked efforts.

## Recalculation

Segment recomputation is deterministic for the same stored streams and definition. It replaces calculated efforts; it never changes immutable source files.

When diagnosing a missing match, confirm owner/sport and direction, inspect timestamp gaps and start/end geometry, increase tolerance gradually, then recompute and review the effort table.
