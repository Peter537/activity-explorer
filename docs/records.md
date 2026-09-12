# Records and best efforts

Activity Explorer calculates records only from activity files imported into the selected local profile. Benchmark names are code-defined; no record values are downloaded from or seeded from Garmin or Strava.

Only achieved records appear on the **Records** page. The benchmark name opens its attempt history; the activity name underneath opens the source activity. Each result includes its profile, date, and source-data coverage. The table keeps these fields visible when rows restack on narrow screens.

## Benchmark attempt history

Open any benchmark to compare qualifying attempts for its sport, selected profile (or All profiles), and training scope. This includes activity records, distance bests, timed distance bests, and power bests. Attempts are ordered strongest first using unrounded values, with 50 rows per page and no overall result limit. Equal results sort by activity date, activity ID, and position in the recorded stream.

**Best only** is the default: each activity contributes its best qualifying attempt. **Multiple, non-overlapping** keeps the strongest attempt in each activity, then the strongest remaining attempt that shares no part of the recorded stream with an already selected attempt. This can yield fewer attempts than a selection that prioritizes fitting the most attempts. For example, a fastest 5 km in the middle of a 10 km activity can exclude two slower adjacent 5 km efforts. A 6 km activity can contribute at most one 5 km attempt.

Adjacent attempts may share an exact endpoint, including one between recorded samples. Riding or running the same road again later can count as another attempt. Non-overlap applies separately to each activity and benchmark: a 5 km attempt and a 10 km attempt may use the same portion of an activity. There is no intensity threshold or requirement that an effort was a deliberate attempt.

Rows show result, activity, profile, date, coverage, and elapsed start–finish position. Positions include milliseconds to distinguish interpolated endpoints. Whole-activity benchmarks contribute one row per qualifying activity, show **Whole activity**, and have no attempt-mode selector.

The detail route is `/records/attempts`, with sport, kind, and key parameters identifying the benchmark; optional scope, owner, mode, and page parameters preserve the view. It has no separate sidebar entry. Direct links, refresh, and browser Back/Forward work normally. Changing profile keeps the benchmark and scope and resets pagination. **Back to Records** restores the sport and scope.

Attempt history is calculated on demand from the current stored summaries and streams; it does not create a second persisted record set. The query filters activities before reading streams and processes payloads in batches of 16. Whole-activity records do not read stream payloads. Refresh to include later imports, corrections, transfers, or deletions. Loading failures offer a retry instead of presenting an incomplete list as complete.

Attempts use the qualification rules below. Selecting multiple attempts does not relax power coverage requirements or turn a newly cut boundary into a recording gap. The Records overview and dashboard continue to use the existing derived winning snapshots.

## Sport selection

The Records page shows one sport at a time. Use the icons and sport names above the tables to switch sports. The selector lists sports with achieved records in All training for the selected profile, in Cycling, Running, Walking, and Rowing order. Sports remain available when you change record scope.

Without an explicit sport choice, the page opens the available sport with the most imported activities across indoor and outdoor training. Ties use the selector order. All profiles counts stored activities across all profiles. A sport without calculated records cannot become the default.

Selecting a sport adds its lowercase name to the URL, for example `/records?sport=rowing&scope=indoor`. Refresh and browser Back or Forward restore explicit choices. Changing scope preserves the sport. Changing profile also preserves it when available; otherwise the page removes the sport parameter and selects the new profile's default. Unsupported sport values are removed independently of scope. No last-used sport preference is stored.

When a sport has no records in the selected scope, the page keeps it selected and offers **View all training**. A profile without records shows an import-oriented empty state without the selector.

## Record scope

**All training (including indoor)** is the default. **Indoor only** includes activities classified as indoor or virtual, plus GPS-less activities without an explicit outdoor classification. **Outdoor only** excludes these activities. The route-local choices use `/records?scope=indoor` and `/records?scope=outdoor`; the default `/records` URL omits the query value. Refresh and browser Back or Forward restore the selected scope, while unsupported scope values normalize to All training without discarding a valid sport choice.

Classification uses the strongest imported evidence available:

- FIT indoor, treadmill, spin, and virtual sub-sports are indoor; clearly outdoor sub-sports are outdoor.
- Strava and XML activity labels containing indoor, treadmill, trainer, spin, or virtual are indoor. An explicit outdoor label is outdoor.
- When subtype evidence is absent, a usable GPS track is outdoor. A no-GPS activity remains indoor or unknown and is excluded from **Outdoor only**.

Imported subtype evidence overrides the GPS fallback. Virtual training counts as indoor.

Record recomputation builds separate, complete winner sets for all three scopes. Each activity contributes to All training and its applicable Indoor or Outdoor set. An indoor winner in the default scope is replaced by the next-best eligible outdoor result in Outdoor only. Scope is stored with each derived snapshot and remains owner-isolated.

## Activity records

Cycling, Running, and Walking can produce:

- longest distance;
- longest moving time;
- most elevation gain;
- best average speed among activities of at least 1 km.

Rowing produces longest distance, longest moving time, and best average split for activities of at least 1 km. The average split uses distance divided by moving time, displayed as time per 500 m. Rowing does not produce elevation records.

## Distance bests

Cycling uses these targets in display order:

5 km, 5 miles, 10 km, 10 miles, 20 km, 30 km, 40 km, 50 km, 80 km, 50 miles, 90 km, 100 km, 100 miles, 180 km, and 200 km.

Running and Walking use:

400 m, 1/2 mile, 1 km, 1 mile, 2 miles, 5 km, 10 km, 15 km, 10 miles, 20 km, half marathon, 30 km, marathon, and 50 km.

Rowing uses:

100 m, 500 m, 1 km, 2 km, 5 km, 6 km, 10 km, half marathon (21,097 m), and marathon (42,195 m).

Distance bests require valid timestamps. Cycling, Running, and Walking also require finite GPS coordinates; Rowing can instead use valid per-point recorded distance without GPS. All use elapsed time, so pauses count. For each edge, the calculation prefers the source's finite, nondecreasing cumulative-distance delta. If recorded distance is absent, it falls back to Haversine distance between valid GPS coordinates. Crossing time is interpolated at the exact benchmark distance rather than rounded to the next complete sample.

Recording gaps and stationary pauses remain inside the candidate stream. Their time counts toward the result, including when recorded distance stays unchanged or geometry supplies a zero-distance edge. A gap is therefore not a segmentation boundary by itself.

A new candidate segment starts after any of these boundaries:

- a missing or invalid timestamp, or a coordinate required by the sport;
- a nonpositive or reversed timestamp interval;
- a recorded distance-counter reset or another unusable distance edge;
- a derived edge speed above 200 km/h for Cycling, 60 km/h for Running, or 30 km/h for Walking and Rowing.

An edge exactly at the sport limit remains eligible. A benchmark window never crosses one of these boundaries.

GPS-free indoor and virtual rowing can produce distance bests from valid per-point recorded distance in All training and Indoor only. Summary distance alone never qualifies. Cycling, Running, and Walking fixed-distance bests continue to require GPS.

## Timed distance bests

Timed distance bests report the greatest distance covered within an exact elapsed-time window. Cycling uses these targets in display order:

5 min, 10 min, 20 min, 30 min, 1 hour, 2 hours, and 4 hours.

Running and Walking use:

5 min, 10 min, 15 min, 30 min, 1 hour, and 2 hours.

Rowing uses 1 min, 4 min, 30 min, and 1 hour. Its distance and time targets follow the individual [Concept2 rowing ranking events](https://log.concept2.com/help/#ranking). Activity Explorer calculates rolling personal training bests from imported streams; it does not verify or submit Concept2 ranking results.

The calculation interpolates cumulative distance at the exact start and finish of each window and considers windows anchored at both start and finish samples. Pauses, stationary periods, and ordinary recording gaps remain inside the window, so their time counts. Incomplete windows and windows with no distance do not qualify; every displayed timed distance best has 100% duration coverage.

For each edge, the calculation prefers a finite, nondecreasing per-point recorded-distance delta and otherwise falls back to valid GPS geometry. A new candidate segment starts at an invalid or reversed timestamp, a missing usable distance edge, a recorded-distance reset, or an edge above the sport speed limit listed under **Distance bests**. An edge exactly at the limit remains eligible.

Timed distance bests for every sport can use a GPS-less indoor or virtual stream when every edge in the candidate window has valid per-point recorded distance. Those results appear in All training and Indoor only. Summary distance alone never qualifies.

## Power bests

Power bests are calculated for Cycling, Running, Walking, and Rowing whenever the imported stream contains recorded power samples. The targets are:

5, 15, and 30 seconds; 1, 2, 3, 5, 8, 10, 15, 20, 30, and 45 minutes; and 1 and 2 hours.

Each result is the highest time-weighted average power found in a qualifying stream window. Windows require at least 98% duration coverage, and a power-sample gap over five seconds splits the stream. Power is never inferred from speed, heart rate, or activity summaries. Indoor activities with recorded power samples are eligible without GPS in All training and Indoor only, but not in Outdoor only.

## Recalculation

Imports automatically recalculate all three record scopes for the affected owner. Record computation version 7 adds Rowing and Indoor only. On startup, the repair worker replaces an owner's complete derived snapshot set when an expected All, Indoor, or Outdoor set is missing or any snapshot predates version 7. Existing version-6 snapshots backfill automatically. An owner with any snapshot from a version newer than 7 is left untouched, preventing an older build from replacing newer derived data. Repeating the repair is safe and leaves current snapshots unchanged. Recalculation does not alter imported originals, user edits, profile assignments, gear, or custom metrics.

Supported FIT sub-sports explicitly classify indoor, treadmill, spin, virtual, and clearly outdoor training. Generic cycling, running, walking, and rowing labels defer to the GPS fallback described above.
