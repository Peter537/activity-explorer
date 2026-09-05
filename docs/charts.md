# Understanding charts

Activity Explorer charts show recorded or calculated values without inventing samples between them. Visible axes and gridlines establish the scale; pointer, keyboard, and tabular inspection provide the exact retained values behind the line.

~~~mermaid
flowchart LR
    Stream["Recorded stream positions"] --> Stats["Summary average, range, and coverage"]
    Stream --> Series["Gap-aware representative series"]
    Series --> Plot["Labelled axes, gridlines, and line"]
    Plot --> Pointer["Pointer: nearest retained sample"]
    Series --> Slider["Independent keyboard slider"]
    Series --> Table["Independent data table"]
    Stream --> Missing["Missing metric or recording gap"]
    Missing --> State["Visible break or unavailable chart"]
~~~

Range, coverage, and the ordinary sample average are calculated from the complete usable source series. The plotted line and inspection controls use a smaller representative series when dense recordings need to be downsampled. Selected segment efforts override the heading average with the persisted comparison metric so it agrees with the effort table.

## Reading the chart frame

The horizontal axis identifies where a value occurs: calendar month on the dashboard, elapsed time or distance on detailed streams, and distance on a segment definition. The vertical axis uses the metric and unit shown in the chart heading, such as metres, beats per minute, watts, or minutes per kilometre. Gridlines make changes in height comparable to labelled values rather than only to the chart card.

Detailed stream headings summarize the full usable series:

- **Sample average** is the arithmetic mean of usable metric samples and is displayed with the metric unit on ordinary activity charts.
- **Range** is the lowest through highest usable value. Both endpoints are displayed with the same unit.
- **Coverage** is the percentage of stream positions that have both a usable horizontal-axis position and a finite metric value.

These figures do not change when the display selects representative points for a dense line.

## Dashboard monthly distance

**Distance over the last 12 months** totals imported activity distance by calendar month. Five horizontal labels span the 12-month period, while the vertical axis and gridlines show distance in kilometres. Pointer inspection identifies the month and its exact total, including a zero-distance month. Open **Inspect monthly values** to use the keyboard-operable month slider or view all 12 Month/Distance rows in a table.

## Activity and effort streams

Rowing pace uses minutes per 500 m in charts and time per 500 m in summary cards and lap splits. Stroke rate uses strokes per minute (spm). Recorded stroke totals appear as Total strokes in activity details. Indoor rowing needs no GPS for its distance axis when per-point distance was recorded; empty rowing elevation panels are omitted.

Activity details and a selected segment effort use the same chart system for elevation, speed or pace, heart rate, cadence, power, temperature, and respiration when those fields were recorded. On a current selected effort, the speed or pace heading is labelled **Segment elapsed average** and uses saved segment distance divided by elapsed time. Sensor headings are labelled **Time-weighted average** and use the persisted timestamp-weighted value. A legacy effort is explicitly labelled **Legacy sample average** until recomputed. The plotted samples and displayed range remain the original recorded values in every case.

Choose **Elapsed time** or **Distance** to change the shared horizontal axis. A selected effort's distance begins at `0 m`; whole-activity distance values are not carried into the effort chart. Moving the pointer across any populated chart places every other populated chart in that group at the same axis position. Each chart reports its own nearest retained representative sample, so values remain truthful when sensors were recorded at different intervals or have gaps.

Open **Inspect exact values** on one chart to step through that chart with a keyboard-operable slider or to open its data table. This inspector is intentionally independent: it does not move the other charts. Pointer comparison is the synchronized path; the slider and table provide a stable per-chart alternative for keyboard and assistive-technology users.

## Segment definition elevation profile

The segment definition profile keeps its more detailed presentation: distance on the horizontal axis, elevation on the vertical axis, labelled gridlines, and a grade-colored area. Pointer or keyboard inspection reports exact distance, elevation, and local grade and marks the corresponding definition point on the map. The legend explains the grade bands, while the data table remains available without relying on color or pointer input.

This definition profile describes the saved path. The charts for a selected effort describe the recorded activity-stream slice and follow the synchronized stream behavior above. See [Local segment methodology](segments.md) for grade calculation, rendering limits, and matching details.

## Missing data, gaps, and dense recordings

**Not recorded in the source** means the selected stream contains no usable samples for that metric. Activity Explorer does not infer a missing sensor value, and an unavailable chart does not show meaningless axes or gridlines. A single usable sample still renders as one point with its chart frame.

A missing metric sample or a recording gap splits the plotted line; the chart does not draw a connection across unavailable data. Coverage falls when only part of the stream contains the metric.

Dense streams are reduced to a bounded representative series for rendering and inspection. Bucket extrema preserve important lows and highs, and gaps remain separate. Average, range, and coverage continue to use the complete usable series, while pointer, slider, and table values identify retained representative samples.
