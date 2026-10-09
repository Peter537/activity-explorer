# Maps

Activity Explorer serves MapLibre GL JS 6.2.0 as local ESM assets. One persisted global setting controls every world, activity, route, segment, and drawing map.

## Blank by default

The first-run default is `Blank`. Before initialization, each map reads that setting and receives an in-memory style with no remote sources. In blank mode:

- no OpenFreeMap preconnect, style, tile, glyph, or sprite request is made;
- activities, routes, segments, highlighted efforts, and editable drawing points still render;
- local attribution identifies Activity Explorer tracks;
- the app remains usable offline.

The Playwright regression captures browser requests and fails if blank world-map rendering contacts a third-party host.

## Online opt-in

Under **Settings**, enable OpenFreeMap to opt in globally. The choice persists in SQLite and takes effect after the app reloads. The default configured style is:

~~~text
https://tiles.openfreemap.org/styles/liberty
~~~

Style and tile requests reveal the viewed area and normal connection metadata to the configured provider. Stored source files and GeoJSON tracks remain on the local same-origin host. Online maps display visible OpenStreetMap contributor attribution. A disclosure appears on maps that can contact the provider.

The Content Security Policy permits OpenFreeMap network origins only while online mode is enabled. If the configured online style fails, the browser falls back to the local blank style.

## Viewport behavior

Activity, route, and segment endpoints validate complete finite west/south/east/north bounds, coordinate ranges, reporting periods, dates, and zoom. Malformed internal requests receive a structured HTTP 400 response rather than becoming a server error. Responses are owner/sport filtered and capped; activity dates are filtered before the cap. Numeric indexes support route viewport pruning.

MapLibre renders repeated world copies and can therefore report viewport longitudes outside `[-180, 180]`. Before requesting GeoJSON, Activity Explorer wraps those longitudes into the accepted range. A viewport at least 360° wide is sent as the full world (`west=-180`, `east=180`); a narrower viewport crossing the antimeridian uses `west > east`.

Stored geometry continues to use the smallest circular interval, so a short route near ±180° is not treated as spanning the world. Latitude padding for segment candidate searches accounts for longitude scale at high latitude.

## Reporting dates and URLs

Activities and World Map share a **Period** control. Dates affect activity lines only; routes and segments retain their owner/sport filters. Choose **Apply filters** after editing. The map retains its previous results with an unapplied-filter notice until the new selection is valid and applied.

The map reports loading, an empty viewport, or a line-query failure. **Retry map lines** retries a failed query without advancing the selected period. Superseded viewport requests are cancelled so an older response cannot replace the current view.

| Period | URL value | Inclusive dates |
| --- | --- | --- |
| Today | `period=today` | Owner's local today |
| This week | `period=this-week` | Current Monday–Sunday |
| Last week | `period=last-week` | Previous Monday–Sunday |
| This month | `period=this-month` | Full current calendar month |
| Last month | `period=last-month` | Full previous calendar month |
| Year to date | `period=year-to-date` | January 1 through today |
| Last year | `period=last-year` | Full previous calendar year |
| All time | No date parameters | All dates |
| Custom range | `from=yyyy-MM-dd` and/or `to=yyyy-MM-dd` | Supplied endpoints, either optional |

Each activity belongs to its local start date in its owner's **Reporting timezone**, defaulting to `Europe/Copenhagen`. In **All profiles**, relative dates resolve separately for each owner. The control shows a shared range when dates agree and owner-labelled ranges when they differ. Switching to Custom range prefills only a common range.

A recognized preset takes precedence over supplied `from` and `to`; canonical URLs remove the ignored dates. `period=all-time` and `period=custom` are accepted explicit values. Canonical All time URLs omit date parameters, while custom URLs keep fixed endpoints. Unknown periods, invalid dates, reversed ranges, and unsupported calendar boundaries show validation errors and preserve supplied values. An invalid initial URL does not load unfiltered results. An unavailable timezone links to Profiles for correction.

Refresh, bookmarks, and browser Back/Forward restore the URL selection and resolve relative dates again. Applying filters or changing profile also resolves them again. Activities sorting and pagination retain the captured period; map viewport requests carry an internal `asOf` round-trip UTC timestamp so panning does not advance the displayed period. Public page URLs remain relative and never contain that reference time. World Map also stores `sport` in the URL. Profile selection remains session-wide application context and is not written into public route URLs.

## Detail and drawing maps

Detail maps render continuous track lines. Orange point markers appear only on editable drawing maps; non-editable activity, route, and segment maps do not render every track point as a marker. The MapLibre canvas owns the keyboard focus stop and receives a contextual accessible name. Editable maps also reference the visible drawing instructions.

Detail maps pass bounded local coordinates to the same component and fit the line without requiring a basemap. Drawing maps expose keyboard instructions and an **Add point at map center** action, plus keyboard-operable undo/clear controls and a live point-count/coordinate status.

## Changing providers

`Maps:StyleUrl` is the only configured online style URL. A provider change also requires:

1. attribution and license review;
2. CSP origin changes;
3. privacy documentation updates;
4. blank-mode zero-request and online-attribution browser tests;
5. a review of any required glyph, sprite, or data origins.

Do not add an unconditional preconnect or CDN dependency.

## Deliberate exclusions

Version 0.1.0 has no geocoding, remote route planning, road/trail snapping, turn-by-turn navigation, background tile downloading, PostGIS, SpatiaLite, or remote spatial database.

If lines are missing, clear filters, choose **All profiles**, and open the relevant detail page. If only the basemap is blank, check the global setting and provider availability; local geometry does not depend on the online service.
