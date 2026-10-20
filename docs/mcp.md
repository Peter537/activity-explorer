# Local MCP server

`ActivityExplorer.Mcp` 0.1.0 is a separate .NET 10 executable for desktop and IDE MCP clients. It uses the official C# SDK 2.2.0, supports the current `2026-07-28` protocol and legacy `2025-11-25` clients, and communicates over stdio. The web application can continue running against the same local database.

## Build and connect

Initialize the database and import activities through the web application first. Publish the MCP executable from the repository root:

~~~powershell
dotnet restore ActivityExplorer.slnx --locked-mode
dotnet publish src/ActivityExplorer.Mcp/ActivityExplorer.Mcp.csproj --configuration Release --no-restore --output artifacts/mcp -p:UseAppHost=false
~~~

Configure the client's stdio server with command `dotnet`, the **absolute path** to the published `ActivityExplorer.Mcp.dll` as its argument, and `ACTIVITY_EXPLORER_DATA` pointing to the existing data directory. The .NET 10 runtime must be installed. Keep the complete published directory together. Launching the DLL avoids building or restoring packages when a client connects.

For clients using the common `mcpServers` configuration shape, adapt these example paths:

~~~json
{
  "mcpServers": {
    "activity-explorer": {
      "command": "dotnet",
      "args": ["C:/ActivityExplorer/mcp/ActivityExplorer.Mcp.dll"],
      "env": {
        "ACTIVITY_EXPLORER_DATA": "C:/ActivityExplorer/data"
      }
    }
  }
}
~~~

Client configuration formats vary. There is no HTTP endpoint or listening port. Each client launches its own process; closing stdin shuts it down. Logs go to stderr, and stdout contains only MCP messages.

## Read-only contract

This release exposes read-only tools only. It opens SQLite with `Mode=ReadOnly`, validates the existing schema without upgrading it, and registers no import, watched-folder, repair, recovery, staging or mutation services. It creates no application directories or file logs. Missing databases and incompatible schemas fail startup with an actionable stderr diagnostic.

Read-only applies to application data. SQLite can require WAL/SHM coordination files while accessing a live database. Do not mark a database immutable while the web application can modify it, and use a local filesystem. A database that needs recovery or an upgrade must be opened through the matching web application.

Read-only does not hide the data returned to the client or its model provider. Select the permitted profiles in startup configuration when the client should see only part of the local library. Full tracks, GPS geometry, original files and exports are not exposed. Imported names and descriptions are returned as data, never executed as instructions.

## Configuration

The data-root rule matches the web app: `ACTIVITY_EXPLORER_DATA`, otherwise the user's local application-data directory under `Activity Explorer`. Other options use the `Mcp` configuration section. Set environment variables with double underscores, for example `Mcp__RequestTimeoutSeconds=90`, or pass arguments such as `--Mcp:RequestTimeoutSeconds 90`. Options are captured at startup; appsettings files are not loaded.

| Option | Default | Supported values |
| --- | --- | --- |
| `AccessMode` | `ReadOnly` | Only `ReadOnly`; other modes fail startup |
| `AllowedProfileIds` | All profiles | Optional nonempty GUID array, e.g. `Mcp__AllowedProfileIds__0` |
| `RequestTimeoutSeconds` | 60 | 1–3600; cooperative cancellation |
| `MaxConcurrentCalls` | 4 | 1–32 |
| `MaxConcurrentAnalyses` | 1 | 1 through `MaxConcurrentCalls` |
| `CapacityWaitSeconds` | 5 | 1–60 |
| `MaxResponseBytes` | 1048576 | 1024–16777216 |

An invalid option, unknown `Mcp` key or malformed profile allowlist fails startup. Options cannot be changed by a tool. The allowlist applies both to discovery of profiles and to subsequent entity queries. Every profile-scoped call requires an explicit `profileId`; selecting a profile in the browser does not affect MCP.

## Tools

| Tool | Result |
| --- | --- |
| `get_server_status` | Version, stdio transport, read-only mode, database readiness and limits |
| `list_profiles` | Permitted profiles and reporting timezones |
| `list_activity_filter_values` | Paginated `device`, `gear` or `tag` values |
| `search_activities` | Filtered activity page and complete matching totals |
| `get_activity` | Scalar measurements, tags and related collection counts |
| `get_dashboard` | Totals, comparison, sports and bounded trends |
| `get_training_calendar` | Owner-local daily and weekly summaries for one month |
| `get_records` | Benchmark definitions, record values and calculation availability |
| `get_record_attempts` | Ranked attempts for a benchmark returned by `get_records` |
| `search_segments` | Matching segment definitions and effort counts |
| `get_segment` | Definition summary, relationship counts and metric status |
| `list_segment_efforts` | Filtered, paginated saved efforts |
| `compare_segment_efforts` | Two explicit efforts, elapsed delta and at most 100 timing samples |
| `search_routes` | Matching route summaries |
| `get_route` | Scalar route measurements and description |

Use `list_profiles` first. `search_activities` accepts a `criteria` object matching the application's activity filters, a `dates` object such as `{"period":"custom","from":"2026-01-01","to":"2026-01-31"}`, an activity sort and pagination. Tools advertise input and output schemas. Enums use camelCase strings, dates use ISO calendar dates, distances are metres, durations seconds, and power watts.

Relative dates capture one server instant and resolve in the selected profile's reporting timezone. Responses include resolved date ranges. Activity search defaults to all time, dashboard to year-to-date/monthly, calendar to the profile's current month, and records to all time.

List pages default to 25 with a maximum of 100; page numbers range from 1 to 1000. Record attempts use pages of 50. Invalid pagination is rejected. Dashboard requests exceeding 120 trend buckets are rejected before activity materialization; use a narrower period or monthly grouping. `search_activities` still provides complete all-time totals. Oversized responses fail explicitly rather than returning cut-off JSON.

All-time records identify their source as `persisted_snapshot`. Missing snapshots or incompatible calculation versions produce an explicit unavailable result and are never repaired by MCP. Matching calculation versions do not prove that every concurrent import has finished recomputation. Selected-period records are calculated in memory without persistence. Comparison failures retain their domain-specific unavailable reasons; absent measurements remain null rather than zero.

Successful and failed tool results use `{ "data": ..., "error": ..., "asOfUtc": ... }`, in both structured content and compatible text content. Tool failures set `isError`; their error object contains a stable code, message and retryable flag. Busy and timeout errors can be retried with a narrower request. Client cancellation discards partial calculations. Request deadlines are cooperative, not a process-level hard time limit.

Exit codes are 0 for normal shutdown, 2 for invalid configuration, 3 for unavailable/incompatible storage and 1 for unexpected fatal failure.

## Development and verification

Run the full release checks in [Testing](testing.md). Focused MCP tests use:

~~~powershell
dotnet test tests/ActivityExplorer.Tests/ActivityExplorer.Tests.csproj --configuration Release --no-build --no-restore --filter-class 'ActivityExplorer.Tests.Mcp*'
~~~

Tests use isolated fictional data, exercise file-backed read-only connections in both journal modes, and launch the built MCP executable through the SDK for modern and legacy protocol calls. The subprocess path is supplied by MSBuild; tests do not build or restore their child process. Existing web tests continue to cover the shared readers through their original service interfaces.

HTTP, writable tools, exports, raw tracks and additional read capabilities require separate implementation. The web app's owner mutation locks are process-local, so writable MCP operations also need a coordinated database/file mutation design.
