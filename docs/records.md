# Records and best efforts

Activity Explorer calculates records only from activity files imported into the selected local profile. Benchmark names are code-defined; no record values are downloaded from or seeded from Garmin or Strava.

Only achieved records appear on the **Records** page. Each result links to its source activity and includes its profile, date, and source-data coverage. The table keeps these fields visible when rows restack on narrow screens.

## Record scope

**All training (including indoor)** is the default. Select **Outdoor only** to exclude activities classified as indoor or unknown. The route-local choice is represented by `/records?scope=outdoor`; the default `/records` URL omits the query value. Refresh and browser Back or Forward restore the selected scope, while unsupported values normalize to the default.

Classification uses the strongest imported evidence available:

- FIT indoor, treadmill, spin, and virtual sub-sports are indoor; clearly outdoor sub-sports are outdoor.
- Strava and XML activity labels containing indoor, treadmill, trainer, spin, or virtual are indoor. An explicit outdoor label is outdoor.
- When subtype evidence is absent, a usable GPS track is outdoor. A no-GPS activity remains indoor or unknown and is excluded from **Outdoor only**.

Imported subtype evidence overrides the GPS fallback. Virtual training counts as indoor.

Record recomputation builds separate, complete winner sets for all training and outdoor-only training. This means an indoor winner in the default scope is replaced by the next-best eligible outdoor result in **Outdoor only**, rather than leaving that benchmark blank. Scope is stored with each derived snapshot and remains owner-isolated.

## Activity records

Every supported sport can produce:

- longest distance;
- longest moving time;
- most elevation gain;
- best average speed among activities of at least 1 km.

## Distance bests

Cycling uses these targets in display order:

5 km, 5 miles, 10 km, 10 miles, 20 km, 30 km, 40 km, 50 km, 80 km, 50 miles, 90 km, 100 km, 100 miles, 180 km, and 200 km.

Running and Walking use:

400 m, 1 km, 1/2 mile, 1 mile, 2 miles, 5 km, 10 km, 15 km, 10 miles, 20 km, half marathon, 30 km, marathon, and 50 km.

Distance bests require valid timestamps and finite GPS coordinates. They use elapsed time, so pauses count. For each edge, the calculation prefers the source's finite, nondecreasing cumulative-distance delta. If recorded distance is absent, it falls back to Haversine distance between the GPS coordinates. Crossing time is interpolated at the exact benchmark distance rather than rounded to the next complete sample.

Recording gaps and stationary pauses remain inside the candidate stream. Their time counts toward the result, including when recorded distance stays unchanged or geometry supplies a zero-distance edge. A gap is therefore not a segmentation boundary by itself.

A new candidate segment starts after any of these boundaries:

- a missing or invalid timestamp or coordinate;
- a nonpositive or reversed timestamp interval;
- a recorded distance-counter reset or another invalid distance edge;
- a derived edge speed above 200 km/h for Cycling, 60 km/h for Running, or 30 km/h for Walking.

An edge exactly at the sport limit remains eligible. A benchmark window never crosses one of these boundaries.

Indoor and virtual activities without GPS cannot produce distance bests, even if the activity has a summary distance. They can still produce activity records and power bests in **All training**.

## Timed distance bests

Timed distance bests report the greatest distance covered within an exact elapsed-time window. Cycling uses these targets in display order:

5 min, 10 min, 20 min, 30 min, 1 hour, 2 hours, and 4 hours.

Running and Walking use:

5 min, 10 min, 15 min, 30 min, 1 hour, and 2 hours.

The calculation interpolates cumulative distance at the exact start and finish of each window and considers windows anchored at both start and finish samples. Pauses, stationary periods, and ordinary recording gaps remain inside the window, so their time counts. Incomplete windows and windows with no distance do not qualify; every displayed timed distance best has 100% duration coverage.

For each edge, the calculation prefers a finite, nondecreasing per-point recorded-distance delta and otherwise falls back to valid GPS geometry. A new candidate segment starts at an invalid or reversed timestamp, a missing usable distance edge, a recorded-distance reset, or an edge above the sport speed limit listed under **Distance bests**. An edge exactly at the limit remains eligible.

Unlike fixed-distance bests, timed distance bests can use a GPS-less indoor or virtual stream when every edge in the candidate window has valid per-point recorded distance. Those results appear only in **All training**. Summary distance alone never qualifies, and fixed-distance bests continue to require GPS.

## Power bests

Power bests are calculated for Cycling, Running, and Walking whenever the imported stream contains recorded power samples. The targets are:

5, 15, and 30 seconds; 1, 2, 3, 5, 8, 10, 15, 20, 30, and 45 minutes; and 1 and 2 hours.

Each result is the highest time-weighted average power found in a qualifying stream window. Windows require at least 98% duration coverage, and a power-sample gap over five seconds splits the stream. Power is never inferred from speed, heart rate, or activity summaries. Indoor rides with recorded smart-trainer or power-meter samples are eligible without GPS in **All training**, but not in **Outdoor only**.

## Recalculation

Imports automatically recalculate both record scopes for the affected owner. Record computation version 6 adds timed distance bests and supersedes version-5 snapshots. On startup, the repair worker replaces an owner's complete derived snapshot set when an expected all-training or outdoor set is missing or any snapshot predates version 6. Existing version-5 snapshots therefore backfill automatically to version 6. An owner with any snapshot from a version newer than 6 is left untouched, preventing an older build from replacing newer derived data. Repeating the repair is safe and leaves current snapshots unchanged. Recalculation does not alter imported originals, user edits, profile assignments, gear, or custom metrics.

Supported FIT sub-sports explicitly classify indoor, treadmill, spin, virtual, and clearly outdoor training. Generic cycling, running, and walking labels defer to the GPS fallback described above.
