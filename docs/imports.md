# Importing data

Activity Explorer accepts user-supplied exports and files. It never requests account credentials and never automates Garmin or Strava.

## First import

1. Create a profile under **Profiles**.
2. Open **Imports** and select the owner profile.
3. Choose one workflow:
   - **Official account-export ZIP** for the Garmin account export.
   - **Multi-file import** for one or more FIT, TCX, GPX, GZ, or ZIP files.
   - **Watch a computer folder** for an existing directory where you manually copy new files.
4. Queue the files and leave the process running until the durable reports complete.

Large browser uploads stream to disk rather than being retained in the Blazor circuit.

## Garmin account exports

Activity Explorer's supported bulk-history path is a user-requested Garmin account export. This is a manual portability workflow, not an automatic Garmin Connect sync.

### Request and import your complete history

1. In a browser, open [Garmin's official export guide](https://support.garmin.com/en-US/?faq=W1TvTPW8JZ6LfJSfK512Q8) and go to Garmin Account Data Management.
2. Sign in, select **Export Your Data**, and then select **Request Data Export**. Garmin may localize these labels.
3. Wait for Garmin's email containing the download link. Garmin says links are typically sent within 48 hours, but preparation can take up to 30 days.
4. Download the complete outer ZIP and retain your own backup copy.
5. In Activity Explorer, create or select the destination profile and open **Imports &rarr; Official account-export ZIP**.
6. Choose the downloaded outer ZIP and queue it directly. Do not extract, recompress, rename, or rearrange it first. Activity Explorer accepts both known uploaded-file folder families: Garmin documentation names `DI_Connect/DI_Connect-fitness-Uploaded-Files`, while some exports and maintained third-party guidance use `DI_CONNECT/DI-Connect-Uploaded-Files`. Matching is insensitive to case and underscore, hyphen, or space separators, and the same detection applies to nested activity archives.
7. Request and import another complete export whenever you want to refresh the history. Repeated exports are safe: owner-scoped Garmin activity IDs, SHA-256 hashes, and the natural activity fingerprint prevent duplicate activities and redundant source rows.

The importer ignores unrelated wellness files in a recognized account-export layout while retaining all archive protections. It reports unrecognized contents, corrupt activity files, and sports outside Cycling, Running, and Walking instead of silently misclassifying them.

The two recognized folder forms are documented by [Garmin Support](https://support.garmin.com/en-US/?faq=mZi3iyunkt3VSzheytHbz7) and [Gadgetbridge](https://gadgetbridge.org/basics/topics/garmin/import-garmin-connect/) (accessed 2026-08-06). Neither name is treated as the only possible casing or separator form.

### Choose the right Garmin workflow

| Workflow | What it provides | Use it for |
| --- | --- | --- |
| Garmin account export | The complete account archive, including nested archives of original activity files | The first bulk-history import and periodic refreshes |
| Individual **Export Original** | One activity in the original format Garmin received, normally FIT for a Garmin device | Recent activities while waiting for another account export |
| **All Activities &rarr; Export CSV** | Spreadsheet-friendly activity summaries only | Reviewing summary data outside Activity Explorer; it is not a substitute for tracks, laps, or sensor streams |
| Activity Explorer **Local inbox** | Supported files already copied to a folder on this computer | Automatically queuing local files; it does not connect to a Garmin watch, phone, or account |

Because Garmin exports the original activity format, most files recorded by Garmin devices are FIT, while compatible activities originally received in another format may remain GPX or TCX.

### Limits and retention

Activity Explorer 0.1.0 has no supported automatic personal Garmin Connect synchronization. Garmin does provide an official Connect Developer Program, but Garmin currently describes it as an enterprise, business-use program requiring approval; it is not used by this local application. See [Legal and source-export guides](legal-and-exports.md#api-position) for the project decision.

Complete exports can be large. The upload, nesting, entry-count, and expanded-size limits under [Archive protections](#archive-protections) apply. Activity Explorer copies each accepted activity original into private storage, then removes the temporary upload and expanded files when processing finishes or fails. It does not retain the outer account ZIP as a complete Garmin-account backup, so keep the archive you downloaded from Garmin.

## Strava bulk exports

Activity Explorer's supported bulk-history path for Strava is a user-requested account export. It is a manual portability workflow and does not use the Strava API or provide automatic synchronization.

### Request and import your Strava history

1. Sign in to Strava on its website.
2. Open your profile menu in the upper-right corner, select **Settings**, and then open **My Account**.
3. Under **Download your account**, select **Get Started** and then **Request download**.
4. Wait for Strava's email containing the download link. Strava says this may take a few hours.
5. Download the complete outer ZIP and retain your own backup copy.
6. In Activity Explorer, create or select the destination profile and open **Imports &rarr; Multi-file import**.
7. Choose the downloaded outer ZIP and queue it directly. Do not extract or rearrange the archive first, and do not decompress individual `.fit.gz`, `.gpx.gz`, or `.tcx.gz` files. Activity Explorer detects `activities.csv`, expands recordings under `activities/`, and ignores supported route files elsewhere in the export.
8. Request and import another export whenever you want to refresh the history. Repeated exports are safe: owner-scoped Strava activity IDs, decompressed-file SHA-256 hashes, and natural activity fingerprints prevent duplicate activities, stored originals, and provenance rows.

Follow [Strava's official export guide](https://support.strava.com/en-us/articles/15401919-exporting-your-data-and-bulk-export) if its navigation changes. The importer uses `activities.csv` to enrich matching recordings with available activity ID, title, description, and gear metadata. A previously imported archive can be queued again after an importer upgrade to fill missing provenance or metadata without duplicating the activity.

### Choose the right Strava workflow

| Workflow | What it provides | Use it for |
| --- | --- | --- |
| Strava account export | The account archive, including `activities.csv` and file-backed recordings under `activities/` | Bulk history and periodic refreshes |
| Individual **Export Original** | One activity in the format originally uploaded to Strava, commonly FIT for Garmin-synchronized recordings | A recent or corrected activity without requesting another full archive |
| Activity-list CSV export | Spreadsheet-friendly summaries without the underlying recording files | Reviewing summaries outside Activity Explorer; it cannot provide complete GPS, lap, or sensor streams |
| Activity Explorer **Local inbox** | Supported files already copied to a folder on this computer | Automatically queuing local files; it does not connect to Strava, Garmin, a watch, or a phone |

The bulk archive can contain `.fit.gz`, `.gpx.gz`, and `.tcx.gz` recordings. Activity Explorer decompresses these wrappers and preserves the underlying activity file as the immutable original, so importing that same decompressed file separately is recognized as an exact duplicate. Strava activities created manually may have a row in `activities.csv` but no file-backed recording; version 0.1.0 does not synthesize an activity from that summary row alone.

### Coverage, limits, and retention

A Strava export is useful for Strava-edited titles, descriptions, gear, and activities that originated outside Garmin. It should not replace a Garmin account export when Garmin Connect is the source of truth. Garmin says that initially linking Garmin Connect to Strava automatically sends the previous year of activities plus future uploads, so older Garmin history may be absent unless it was transferred separately. See [Garmin's connection guidance](https://support.garmin.com/en-US/?faq=4uYoMd5zEt22rg0iehnro9).

Activity Explorer imports only Cycling, Running, and Walking recordings and reports unsupported or corrupt entries. The upload and extraction limits under [Archive protections](#archive-protections) apply. Accepted underlying activity files are copied into private original storage, but the complete outer Strava ZIP is not retained as an account backup. Keep the archive you downloaded from Strava.

Activity Explorer does not import Strava's proprietary segment catalog and never contacts Strava while importing or testing.

## Local segment path files

Segment path files use a separate synchronous workflow under **Segments**. They do not enter the activity import queue and do not become activity originals.

1. Select one GPX, FIT segment/course, TCX, KML, GeoJSON, or JSON-encoded GeoJSON file containing one directional path.
2. Review the destination profile, local sport, name, and matching tolerance.
3. Optionally provide inclusive zero-based start/end indices. Blank values use the first and last point.
4. Confirm the file order as start-to-finish or choose **Reverse direction**.
5. Create the independent local segment and inspect its provenance label and map before relying on matched efforts.

The reader accepts only path geometry needed for matching. FIT activity files use the activity importer; files containing multiple independent paths are rejected. The upload is removed after the request, including on validation failure. SQLite retains only the local geometry plus the safe base file name and normalized format. Provider identifiers, UUIDs, leader times, leaderboard entries, popularity, and other-athlete data are discarded.

This is a generic file workflow, not a provider-catalog import. Import only material you control or are authorized to reuse. See [Local segment methodology](segments.md) and [Legal and source-export guides](legal-and-exports.md#local-segments).

## Individual files

Choose several files in one selection. Browser uploads are streamed separately with at most two uploads active at once; the durable background parser remains single-consumer.

- **FIT** is preferred for complete sensor, timing, lap, device, temperature, respiration, training-effect, and calorie data when those fields exist in the file.
- **TCX** can contain track, lap, heart-rate, cadence, and extension sensor fields.
- **GPX** is useful for position and elevation, with optional extension sensors.
- A single activity file may be wrapped in GZ or ZIP.

A Garmin identifier is accepted only from export filenames such as `123456_ACTIVITY.fit`; a FIT device serial number is never treated as an activity ID. Missing sensors remain missing rather than being inferred.

Only Cycling, Running, and Walking are supported in 0.1.0, including indoor and virtual subtypes that clearly identify one of those sports.

## Deduplication

Deduplication is owner-scoped and follows this order:

1. provider activity ID (for example a Garmin ID recovered from the official filename);
2. SHA-256 of the exact file;
3. owner, sport, start time, duration, distance, and normalized track fingerprint.

A repeated export from the same provider does not create another activity, stored copy, or redundant source row. The same bytes from a different provider may add one distinct provenance row without copying the bytes. Equivalent cross-format files enrich the existing activity.

Local user edits always win. FIT supplies canonical timing, laps, sensors, and track detail; Strava export metadata may fill title, description, and gear; TCX outranks GPX for sensor detail. Imports for one profile never match or mutate another profile.

## Reassigning an activity

Changing an activity's profile is a coordinated transfer, not a merge. The target is preflighted for natural-fingerprint, Garmin/Strava ID, provider/hash, and provider/external-ID collisions; any collision blocks the transfer. Originals are copied to the target owner directory and SHA-256 verified before ownership changes commit. Provenance rows move to a completed target transfer batch so deleting the source profile cannot cascade-delete them.

User-created routes and segments remain with the source profile and are unlinked from the transferred activity. Statistics and segment efforts are recomputed for both profiles.

## Import history

**Import history** is a native disclosure and starts collapsed. Opening it shows the 10 newest entries for the selected profile, ordered by creation time and then import ID. **Show 10 more** increases the cumulative visible limit by 10; closing and reopening the disclosure keeps that list available, and refresh or processing polls retain the chosen limit. Changing the selected profile resets the limit to 10.

Each entry keeps status, counts, profile, workflow type, and time visible. Long processing text remains inside **View processing summary** so a warning or error can be inspected without making every history row tall.
## Watched folders

Under **Imports**, use **Local inbox** to add an existing absolute directory. The app listens for supported files, periodically reconciles missed events, waits for size/write time to stabilize, copies the file to private staging, and never moves, renames, edits, or deletes the source.

This is a convenience for files already present on the computer, not a phone or Garmin Connect sync. Watched folders are not recursive in version 0.1.0. Removing a definition does not delete the directory or any files.

## Archive protections

Version 0.1.0 rejects:

- paths that escape the isolated staging directory;
- symbolic-link entries;
- nesting beyond three archive layers;
- more than 50,000 entries;
- a single declared ZIP entry over 4 GiB;
- more than 20 GiB total expanded content.

The browser activity/archive upload limit defaults to 10 GiB. Route GPX and local segment-path uploads each default to 50 MiB. Uploads stream to `CreateNew` staging files, reject excess bytes with HTTP 413, and remove partial staging after failure. These are safety ceilings rather than hardware recommendations; large archives require corresponding free space for upload, expansion, originals, and database.

## Failure and recovery

Import state is committed to SQLite before processing. One background consumer and owner-scoped mutation locks prevent competing writes. Cancellation sets Interrupted, retains staging, and leaves completion time empty. On restart, queued/interrupted jobs are requeued when staging exists; a missing staged source becomes a clear terminal failure.

Only completed, completed-with-warning, and failed imports delete staging. Each accepted underlying activity original is copied and hash-verified in private storage before cleanup. Route GPX imports create a completed import batch and linked source provenance in the coordinated route operation. Container uploads such as outer Garmin or Strava account ZIPs are not retained as complete provider-account backups.

Import reports persist their status and counts. Diagnostic messages redact user-home paths and do not serialize full tracks or archives.
