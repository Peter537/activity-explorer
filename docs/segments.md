# Local segment methodology

A segment is a local, owner-scoped, directional, sport-specific path. It is not connected to Strava's segment catalog.

## Creating a segment

Activity Explorer supports four local creation workflows:

1. On **Segments**, draw a path directly.
2. On an activity detail, open the focused creator from the **Create segment** header action, then trim the activity visually by distance without exposing source point indices.
3. On a route detail, use the embedded creator to trim the route by distance, inspect the highlighted map path and measured elevation profile, review live distance/ascent/descent/grade metrics, and optionally reverse it.
4. On **Segments**, supply one GPX, FIT segment/course, TCX, KML, or GeoJSON path. Explicitly review the owner, sport, name, tolerance, trim indices, and direction before creating the segment.

Text path formats must contain exactly one directional line. FIT activity files are not accepted by the segment-path workflow; use the normal activity importer and select the required portion instead. MultiLineString or multi-track files must be reduced to one path before import.

The resulting record stores owner and sport, ordered WKB geometry and bounds, distance, elevation summary, tolerance, and limited provenance. Imported-file provenance is only the safe base file name and normalized format. The staged upload is deleted after parsing and is not placed in managed originals. Provider UUIDs and IDs, leader times, leaderboards, popularity, and other-athlete fields are neither exposed by the parser nor persisted.

The shared visual creator starts with the complete GPS path selected. On route detail it remains embedded; on activity detail a focused page keeps the long activity screen compact. Its linked start/end range controls snap to recorded GPS points and provide keyboard-operable backward/forward nudges. The full path remains visible while the selected portion, directional endpoints, distance axis, available elevation samples, and resulting metrics update together. Activity selections retain their original stream positions internally so the saved segment uses the exact inclusive source slice without showing implementation indices. Missing elevation is reported as not recorded and never prevents creating a GPS segment; no synthetic elevation is added. The matching tips are advisory and do not add a minimum distance beyond the requirement for two usable GPS points.

The segment is then compared with historical activities for the same owner and sport. New imports recompute existing segments.

These are independent local definitions. Importing a generic path does not establish Strava identity, catalog parity, provider synchronization, or permission to reuse a third party's data. Supply only files you control or have permission to reuse.

## Matching pipeline

The implementation follows these stages:

1. Use bounding boxes expanded by tolerance to remove impossible activities.
2. Retain original stream indices while rejecting candidates without enough usable GPS points.
3. Group chronological samples near the segment's start and end into proximity runs and consider only ordered start-to-end slices.
4. Resample the segment and each continuous candidate slice at shared distance-relative positions, approximately every 10 metres. Alignment is capped at 50,001 samples; definitions longer than 500 km use evenly spaced normalized samples within that fixed analysis budget.
5. Compare aligned samples in path order so a later nearby return cannot supply an isolated point to an earlier pass.
6. Require at least 95% of aligned samples to fall within the configured tolerance; coverage is that aligned-sample percentage.
7. Reject passes containing a GPS timestamp gap over 30 seconds.
8. Continue after a match to retain repeated passes in the same activity.

The default matching tolerance is 30 metres and applies to both endpoint proximity and aligned path samples. A segment stores an advanced per-segment override for noisy environments.

Reverse-direction travel does not match. The file/drawing order defines start to finish; the explicit reverse option changes the stored order before matching. A nearby parallel road should fail continuous path-alignment checks even if start and end points are close. Loops and switchbacks depend on ordered traversal of one candidate slice, not only geometry intersection or independently nearest points.

## Effort metrics

Each accepted pass records elapsed and moving time; ascent, descent, and average grade; average/maximum speed, heart rate, cadence, and power; average temperature and respiration; coverage; start time; stream indices; and the linked activity. Missing sensors remain null.

Per-owner ranks are recalculated from elapsed time. The inline segment explorer shows the directional path and a grade-colored definition profile, a sortable comparison table, and the selected pass highlighted with start/end points. The profile derives local grade over a disclosed 50 metre distance window, shifts that window at path boundaries, and uses the available span for shorter definitions. Downhill, flat, gentle, moderate, steep, and very steep colors are advisory display categories; exact percentages remain available through pointer, keyboard, and tabular inspection. Inspecting the profile marks the corresponding definition point on the map without moving the viewport.

Local grade is calculated only within contiguous recorded elevation samples. Missing elevation remains a visible gap and is never interpolated across; the whole profile retains the truthful unavailable state when no drawable elevation sequence exists. Rendering uses at most 800 representative samples across all visible runs while retaining path endpoints, important extrema, and representative grade transitions within that budget. The complete stored definition is not downsampled. These local values are ephemeral presentation data and do not replace the persisted whole-segment average grade.

Selecting a pass updates the `effort` URL query and renders the axis-labelled, synchronized charts described in [Understanding charts](charts.md) from that exact activity-stream slice; no duplicate effort stream is stored. Pauses remain part of elapsed time, while moving time excludes stationary samples when usable position/timestamps exist.

## Accuracy limitations

This is a transparent local heuristic, not a certified race timing system. Results depend on GPS sampling and accuracy, device smoothing, timestamps, tunnels/tree cover/urban reflections, pauses, and how precisely the path was selected.

Increasing tolerance can recover a noisy match but also increases false positives, especially around parallel paths and switchbacks. Use the smallest value that covers normal GPS variation and inspect linked efforts.

## Recalculation

Segment recomputation is deterministic for the same stored streams and definition. It replaces calculated efforts; it never changes immutable source files.

When diagnosing a missing match, confirm owner/sport and direction, inspect timestamp gaps and start/end geometry, increase tolerance gradually, then recompute and review the effort table.
