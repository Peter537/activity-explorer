# Data storage and privacy

Activity Explorer processes user-supplied files locally. It does not log in to Garmin or Strava, call their activity APIs, scrape provider sites, add telemetry, or send activity files to a cloud service.

## Application-data directory

The default root is the local application-data path returned by .NET. Set `ACTIVITY_EXPLORER_DATA` before startup to use a different absolute root.

~~~text
Activity Explorer/
  activity-explorer.db
  activity-explorer.db-shm
  activity-explorer.db-wal
  originals/
    <owner UUID>/
      <SHA-256>.<extension>
  staging/
  quarantine/
  logs/
  keys/
~~~

Database metadata stores managed original and staging paths relative to this root. Runtime path guards reject traversal, escape from the expected managed directory, and symbolic-link/reparse traversal. Imported originals are content-addressed and treated as immutable. Exact reimports can share a stored copy.

The application does not alter a browser-selected source or watched-folder source. It also does not delete unexplained files found under `originals/`; untracked files are logged and retained for manual review. Copy, transfer, activity-file quarantine, and profile-quarantine operations have a durable journal that is reconciled after restart.

`keys/` contains ASP.NET Core data-protection keys for interactive sessions and antiforgery tokens. Include it in complete backups and never delete individual keys during normal operation. After intentionally replacing the whole data root, hard-refresh open tabs.

## Import staging

The staging tree follows the durable import state:

- `Completed`, `CompletedWithWarnings`, and `Failed` imports delete their staging directory.
- Cancellation sets `Interrupted`, leaves `CompletedAtUtc` empty, and retains staging.
- Startup requeues `Queued` and `Interrupted` imports whose staged source still exists.
- Missing staging becomes a terminal failure with an actionable report.

Route GPX imports create a completed import batch and a linked `SourceFile`; their staged upload is removed only after the verified original and database provenance are committed.

## What the database contains

SQLite can contain names and notes; timestamps and offsets; location tracks and elevation; speed, heart rate, cadence, power, temperature, respiration, calories, and training fields; routes, local segments and efforts; gear, records, provenance, watched-folder paths, settings, and durable lifecycle journals. Treat the root as private health and location data even when profile names are fictional.

## Network behavior

Every map starts in persistent global `Blank` mode. Blank mode uses local MapLibre assets and an in-memory style and makes zero requests to external map hosts. Activity, route, segment, and drawing maps all read the same setting before initialization.

Enabling OpenFreeMap under **Settings** is an explicit global opt-in. The browser then requests the configured style and tiles, which reveals the viewed area and ordinary network metadata to the provider. Stored tracks remain served from the same-origin local app and are not uploaded to OpenFreeMap. Visible OpenStreetMap attribution remains enabled online.

The host allows only localhost loopback host names. It applies a restrictive Content Security Policy, denies framing, disables MIME sniffing, sends no referrer, and restricts browser permissions. Uploads require both an antiforgery token and the custom same-origin header. These controls do not turn the no-auth app into a safe remote or multi-user service.

## Logs

The host writes console output and bounded rolling files under `logs/`. Configured log levels are honored; exception stacks are retained; multiline input is normalized; sensitive roots are redacted where appropriate; and file-write failures do not escape into application code. Rotation names use invariant UTC time. Treat logs as private because filenames and operational metadata can still be sensitive.

## Complete backups

Activity Explorer does not create automatic database backups. For a complete backup:

1. Stop Activity Explorer.
2. Copy the entire application-data directory, including WAL/SHM files, originals, keys, and quarantine, to encrypted storage.
3. Restart the app.

The profile JSON export contains summarized records only; it is not a backup of source files or the database. A database that already matches the current model remains usable, but older pre-release schemas are unsupported and require a fresh data root and reimport.

## Activity transfer and deletion

Reassigning an activity to another profile first checks fingerprint, Garmin/Strava ID, provider/hash, and external-ID conflicts. Collisions block instead of merging. Originals are copied and SHA-256 verified before ownership changes commit; source rows are rehomed under a completed transfer batch. User-created routes and segments stay with the source profile, lose only their source-activity link, and derived statistics/efforts are recomputed for both profiles.

Activity deletion is permanent. The detail page confirms one named activity; the Activities page can confirm selected rows or an exact snapshot of all IDs matching the current profile and filters. A later import is not added to an already displayed filtered confirmation. Deletion removes streams, laps, supplementary metrics, efforts, and activity provenance. User-created routes, segments, and aggregate import-history entries remain, but source-activity links are cleared and affected segment ranks and records are rebuilt.

An original is removed only when no remaining provenance row references its stored path. The file moves to managed quarantine before the database transaction and is removed after commit. A pre-commit failure restores it. A post-commit cleanup failure leaves a durable journal entry for startup recovery and produces a cleanup-pending warning instead of overstating success. Record snapshots are cleared before recomputation; if that refresh fails, their absence lets the startup statistics repair worker retry.

Profile deletion acquires the owner's mutation lock and blocks while queued, running, or interrupted imports exist. The owner originals directory moves to managed quarantine before database deletion. After commit, quarantine is removed. If filesystem cleanup fails, the profile remains removed and startup recovery retries the cleanup; the UI reports that cleanup is pending instead of claiming complete success.

## Hosting warning

There is no login or authorization layer. Anyone who can connect can inspect location and health data, download originals, edit records, and permanently delete activities or profiles. Do not expose Activity Explorer to a LAN, public bind, reverse proxy, or tunnel. Authentication, authorization, rate limiting, and multi-user isolation are separate, out-of-scope work.
