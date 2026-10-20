# Local MCP server

`ActivityExplorer.Mcp` 0.1.0 is a separate .NET 10 executable for desktop and IDE MCP clients. It uses the official C# SDK 2.2.0, supports the current `2026-07-28` protocol and legacy `2025-11-25` clients, and communicates over stdio. The web application can continue running against the same local database.

## Build and connect

Initialize the database and import activities through the web application first. Publish the MCP executable from the repository root:

~~~powershell
dotnet restore ActivityExplorer.slnx --locked-mode
dotnet publish src/ActivityExplorer.Mcp/ActivityExplorer.Mcp.csproj --configuration Release --no-restore --output artifacts/mcp -p:UseAppHost=false
~~~

Configure the client's stdio server with command `dotnet`, the **absolute path** to the published `ActivityExplorer.Mcp.dll` as its argument, and `ACTIVITY_EXPLORER_DATA` pointing to the existing data directory. The .NET 10 runtime must be installed. Keep the complete published directory together. Launching the DLL avoids building or restoring packages when a client connects.

### Register with Codex

With PowerShell 7 or later, run the setup helper after publishing. Supply the existing data directory used by the web app. This Windows example selects its default data directory; use your custom path instead if the web app uses `ACTIVITY_EXPLORER_DATA`:

~~~powershell
./scripts/register-mcp-codex.ps1 -DataDirectory (Join-Path $env:LOCALAPPDATA 'Activity Explorer') -WhatIf
./scripts/register-mcp-codex.ps1 -DataDirectory (Join-Path $env:LOCALAPPDATA 'Activity Explorer')
codex mcp get activity-explorer
~~~

The [setup script](../scripts/register-mcp-codex.ps1) resolves the published DLL relative to its own checkout, even when invoked from another working directory. It checks for the database, published runtime files, Codex CLI and .NET 10 runtime. It registers absolute executable, DLL and data paths through `codex mcp add`, explicitly selecting `ReadOnly`. `-WhatIf` checks prerequisites without changing registration. It does not build, migrate or open the database.

The helper updates the named server in Codex's user configuration, normally `~/.codex/config.toml`, shared by local desktop, CLI and IDE clients on the same host. It leaves unrelated settings and repository files alone. Rerunning it replaces that server's registration; retain any custom server options separately before doing so. Use `-PublishedDirectory` for a different publish location, `-ServerName` for a separate registration, or `-CodexCommand` / `-DotnetCommand` to select installed executables. Repeat registration after moving the checkout or data directory. Republishing to the same location only requires restarting the server.

Restart the desktop app or IDE extension, or start a new `codex` CLI session. Use `/mcp` to inspect the connection, then ask:

> Use activity-explorer to call get_server_status, then list_profiles.

Confirm that status reports `transport: "stdio"`, `accessMode: "ReadOnly"` and `databaseReady: true`. `list_profiles` should return the permitted profiles, or an empty page if there are none. A registration listed by `codex mcp list` alone does not prove that initialization or a tool call succeeds. No OAuth login is needed for this local server.

Remove the local registration with:

~~~powershell
codex mcp remove activity-explorer
~~~

For manual setup, adapt the [Codex TOML template](../examples/mcp/codex.config.toml). Codex also supports project-scoped `.codex/config.toml` in trusted projects, but that connection applies only within that project. The examples are templates and are not loaded automatically. Keep each user's resolved paths and any profile allowlist in their local configuration. See the [official Codex MCP documentation](https://learn.chatgpt.com/docs/extend/mcp?surface=cli) for client configuration and restart options.

### Other desktop and IDE clients

Adapt the [generic mcpServers JSON template](../examples/mcp/mcpServers.json) for clients using that configuration shape. Replace the DLL and data directory placeholders with absolute paths in the client's user configuration. The command is `dotnet`; if it is unavailable on the client's PATH, use its absolute executable path. Placeholders are literal examples, not environment-variable substitutions.

Client configuration formats vary; select stdio when asked for the transport. There is no HTTP endpoint or listening port. Each client launches its own process; closing stdin shuts it down. Logs go to stderr, and stdout contains only MCP messages. A manually launched server can appear idle while it waits for MCP input.

If startup reports an unavailable or incompatible database, check that the data directory matches the web app's and that the matching web app can open it. If tools do not appear, check the client's server diagnostics and restart the connection after changing configuration. Keep the complete published directory together, and republish after updating server code.

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

All 15 tools are read-only. Parameter names below are the exact JSON names advertised to clients. Use `list_profiles` first and pass the selected `profileId` explicitly to subsequent calls. Unspecified optional filters impose no restriction unless a different default is shown.

| Tool | Required parameters | Optional parameters and defaults |
| --- | --- | --- |
| [`get_server_status`](#get_server_status) | None | None |
| [`list_profiles`](#list_profiles) | None | `page=1`, `pageSize=25` |
| [`list_activity_filter_values`](#list_activity_filter_values) | `profileId`, `kind` | `search`, `page=1`, `pageSize=25` |
| [`search_activities`](#search_activities) | `profileId` | `criteria`, `dates` (all time), `sort="start-desc"`, `page=1`, `pageSize=25` |
| [`get_activity`](#get_activity) | `profileId`, `activityId` | None |
| [`get_dashboard`](#get_dashboard) | `profileId` | `criteria`, `dates` (year to date), `comparison="priorYear"`, `granularity="monthly"` |
| [`get_training_calendar`](#get_training_calendar) | `profileId` | `month` (current month), `criteria` |
| [`get_records`](#get_records) | `profileId`, `sport` | `dates` (all time), `scope="all"` |
| [`get_record_attempts`](#get_record_attempts) | `profileId`, `sport`, `kind`, `key` | `dates` (all time), `scope="all"`, `multiplePerActivity=false`, `page=1` |
| [`search_segments`](#search_segments) | `profileId` | `search`, `sport`, `sort="name-asc"`, `page=1`, `pageSize=25` |
| [`get_segment`](#get_segment) | `profileId`, `segmentId` | None |
| [`list_segment_efforts`](#list_segment_efforts) | `profileId`, `segmentId` | `filter`, `page=1`, `pageSize=25` |
| [`compare_segment_efforts`](#compare_segment_efforts) | `profileId`, `segmentId`, `baselineEffortId`, `comparisonEffortId` | None |
| [`search_routes`](#search_routes) | `profileId` | `search`, `sport`, `sort="name-asc"`, `page=1`, `pageSize=25` |
| [`get_route`](#get_route) | `profileId`, `routeId` | None |

### Shared values and pagination

| Parameter | Type and allowed values |
| --- | --- |
| `profileId`, `activityId`, `segmentId`, `routeId`, `baselineEffortId`, `comparisonEffortId` | Nonempty UUID strings identifying existing entities in the permitted profile |
| `sport` | String: `"cycling"`, `"running"`, `"walking"`, `"rowing"` |
| Filter-value `kind` | String: `"device"`, `"gear"`, `"tag"` |
| Record-attempt `kind` | String: `"distance"`, `"duration"`, `"elevation"`, `"averageSpeed"`, `"distanceEffort"`, `"powerCurve"`, `"timedDistanceEffort"` |
| Record-attempt `key` | String: a benchmark key returned by `get_records`, paired with its matching `kind` and `sport` |
| `scope` | String: `"all"`, `"outdoor"`, `"indoor"` |
| `comparison` | String: `"priorYear"`, `"previousPeriod"` |
| `granularity` | String: `"monthly"`, `"weekly"` |
| `search` | Optional search string; route and segment searches match names, and filter-value searches match values |
| `month` | Optional ISO calendar date identifying the month, for example `"2026-01-01"`; omitted means the profile's current local month |
| `multiplePerActivity` | Boolean, default `false`; requests multiple qualifying attempts from an activity for benchmarks that support them |
| `page` | Integer from 1 to 1000, default 1 |
| `pageSize` | Integer from 1 to 100, default 25 |

`get_record_attempts` always uses pages of 50 and has no `pageSize` parameter. Invalid pagination is rejected. Activity search totals cover every matching activity, independently of the returned page. Distances and ascent are metres, durations are seconds, and power is watts.

### Date selection: `dates`

`search_activities`, `get_dashboard`, `get_records` and `get_record_attempts` accept this optional object. Every field is optional:

| Field | Type and allowed values | Default within the object |
| --- | --- | --- |
| `period` | String: `"all-time"`, `"custom"`, `"today"`, `"this-week"`, `"last-week"`, `"this-month"`, `"last-month"`, `"year-to-date"`, `"last-year"` | `"all-time"` |
| `from` | ISO calendar date, for example `"2026-01-01"` | No lower bound |
| `to` | ISO calendar date, for example `"2026-01-31"` | No upper bound |

Supply `from` or `to` only with `period="custom"`. Custom bounds are inclusive calendar dates in the profile's reporting timezone. Either bound can be omitted, and `from` must not be later than `to`. The maximum `to` date is `9999-12-30`; dates near the calendar limits can also be rejected if timezone conversion would overflow.

Relative dates capture one server instant and resolve in the profile's reporting timezone. Reporting responses include resolved date ranges. Weeks start on Monday.

Omitting the entire `dates` object gives year-to-date for `get_dashboard` and all time for the other three tools. Supplying an empty `dates` object gives all time, including for `get_dashboard`; set `period` explicitly when that distinction matters.

### Activity criteria: `criteria`

`search_activities`, `get_dashboard` and `get_training_calendar` accept this optional object. Every field is optional:

| Field | Type and meaning | Default |
| --- | --- | --- |
| `sport` | Sport string from the values above | Any sport |
| `search` | Search text | No text filter |
| `textMode` | String: `"literal"` or `"words"`; words mode supports quoted phrases | `"literal"` |
| `device` | Device-name text | No device filter |
| `deviceExact` | Boolean controlling exact device matching | `false` |
| `gear` | Gear-name text | No gear filter |
| `gearUnassigned` | Boolean selecting activities without assigned gear | `false` |
| `isIndoor` | Boolean selecting indoor or outdoor activities | Either |
| `hasGps` | Boolean selecting activities with or without GPS | Either |
| `hasPower` | Boolean selecting activities with or without power | Either |
| `minDistanceMeters`, `maxDistanceMeters` | Numeric distance bounds in metres | Unbounded |
| `minMovingSeconds`, `maxMovingSeconds` | Numeric moving-time bounds in seconds | Unbounded |
| `minAscentMeters`, `maxAscentMeters` | Numeric ascent bounds in metres | Unbounded |
| `tagIds` | Array of included tag UUID strings | None |
| `tagMode` | String: `"any"` or `"all"`, controlling matching of included tags | `"any"` |
| `excludedTagIds` | Array of excluded tag UUID strings | None |
| `tagPresence` | String: `"any"`, `"tagged"`, `"untagged"` | `"any"` |

Numeric bounds must be finite and non-negative, with minimum no greater than maximum. A tag cannot be both included and excluded. `tagPresence="untagged"` cannot be combined with included tags. `gear` and `gearUnassigned=true` cannot be combined. Obtain tag IDs with `list_activity_filter_values` using `kind="tag"`.

### Segment effort filter: `filter`

`list_segment_efforts` accepts this optional object. Every field is optional:

| Field | Type and meaning | Default |
| --- | --- | --- |
| `from` | Inclusive ISO calendar date in the profile's reporting timezone | No lower bound |
| `to` | Inclusive ISO calendar date in the profile's reporting timezone | No upper bound |
| `minimumElapsedSeconds` | Numeric elapsed-time minimum in seconds | Unbounded |
| `maximumElapsedSeconds` | Numeric elapsed-time maximum in seconds | Unbounded |
| `sort` | String: `"rank"`, `"date"`, `"elapsed"`, `"speed"`, `"heart-rate"`, `"power"` | `"rank"` |

Elapsed bounds must be finite and non-negative, with minimum no greater than maximum. `from` must not be later than `to`. Sorting uses ascending rank, newest date, shortest elapsed time, or highest speed, heart rate or power, respectively.

### Search ordering: `sort`

| Tool | Allowed values | Default |
| --- | --- | --- |
| `search_activities` | `"start-desc"`, `"start-asc"`, `"distance-desc"`, `"distance-asc"`, `"duration-desc"`, `"elevation-desc"` | `"start-desc"` |
| `search_segments` | `"name-asc"`, `"name-desc"`, `"distance-asc"`, `"distance-desc"` | `"name-asc"` |
| `search_routes` | `"name-asc"`, `"name-desc"`, `"distance-asc"`, `"distance-desc"`, `"elevation-asc"`, `"elevation-desc"` | `"name-asc"` |

### Example calls

Each JSON example is the `params` object for an MCP `tools/call` request: `name` selects the tool and `arguments` contains its inputs. The client handles initialization and the surrounding JSON-RPC message. These are not HTTP requests or shell commands.

Every UUID below is fictional. Replace profile IDs with values from `list_profiles`, activity IDs from `search_activities`, segment IDs from `search_segments`, effort IDs from `list_segment_efforts`, and route IDs from `search_routes`. Keep related entities in the same profile. No personal paths or local-library records are included in these examples.

#### get_server_status

Read the server version, transport, access mode, database readiness and execution limits. No arguments are required.

~~~json
{
  "name": "get_server_status",
  "arguments": {}
}
~~~

#### list_profiles

Discover permitted profiles, activity totals and reporting timezone settings before choosing a profile ID.

~~~json
{
  "name": "list_profiles",
  "arguments": {
    "page": 1,
    "pageSize": 25
  }
}
~~~

#### list_activity_filter_values

Find tag IDs and names for one profile. Set `kind` to `"device"` or `"gear"` for those filter values instead.

~~~json
{
  "name": "list_activity_filter_values",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "kind": "tag",
    "search": "training",
    "page": 1,
    "pageSize": 25
  }
}
~~~

#### search_activities

Find GPS cycling activities of at least 10 km in January 2026, ordered by greatest distance. The response contains a page of activities and totals for all matching activities.

~~~json
{
  "name": "search_activities",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "criteria": {
      "sport": "cycling",
      "hasGps": true,
      "minDistanceMeters": 10000
    },
    "dates": {
      "period": "custom",
      "from": "2026-01-01",
      "to": "2026-01-31"
    },
    "sort": "distance-desc",
    "page": 1,
    "pageSize": 10
  }
}
~~~

#### get_activity

Read scalar activity measurements, tags and related collection counts for an activity returned by search. Full tracks, laps, metric streams and original files are not returned.

~~~json
{
  "name": "get_activity",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "activityId": "22222222-2222-4222-8222-222222222222"
  }
}
~~~

#### get_dashboard

Read cycling totals, a prior-year comparison, sport totals and monthly trends for the year to date. Requests exceeding 120 trend buckets are rejected; narrow the period or use monthly grouping.

~~~json
{
  "name": "get_dashboard",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "criteria": {
      "sport": "cycling"
    },
    "dates": {
      "period": "year-to-date"
    },
    "comparison": "priorYear",
    "granularity": "monthly"
  }
}
~~~

#### get_training_calendar

Read daily, weekly and monthly cycling summaries for January 2026 in the profile's reporting timezone.

~~~json
{
  "name": "get_training_calendar",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "month": "2026-01-01",
    "criteria": {
      "sport": "cycling"
    }
  }
}
~~~

#### get_records

Read all-time outdoor cycling records and benchmark definitions, including their `kind` and `key` values for subsequent attempt queries. The response identifies calculation availability and whether results come from a persisted snapshot or an in-memory calculation.

~~~json
{
  "name": "get_records",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "sport": "cycling",
    "scope": "outdoor",
    "dates": {
      "period": "all-time"
    }
  }
}
~~~

#### get_record_attempts

Read ranked attempts for the longest-distance cycling benchmark. Use the exact `kind` and `key` returned by `get_records`; `"distance"` and `"Longest distance"` are a supported pair. Each page contains at most 50 attempts, including coverage and available recorded interval information.

~~~json
{
  "name": "get_record_attempts",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "sport": "cycling",
    "kind": "distance",
    "key": "Longest distance",
    "scope": "outdoor",
    "dates": {
      "period": "all-time"
    },
    "multiplePerActivity": false,
    "page": 1
  }
}
~~~

#### search_segments

Find cycling segment definitions whose names contain the example search text. Results include effort counts and best elapsed times.

~~~json
{
  "name": "search_segments",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "search": "Example climb",
    "sport": "cycling",
    "sort": "name-asc",
    "page": 1,
    "pageSize": 25
  }
}
~~~

#### get_segment

Read a segment definition summary, parent and child relationship counts, and effort metric calculation status. Geometry and tracks are not returned.

~~~json
{
  "name": "get_segment",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "segmentId": "33333333-3333-4333-8333-333333333333"
  }
}
~~~

#### list_segment_efforts

Read January 2026 efforts lasting from 60 through 3600 seconds, ordered by rank. Use returned effort IDs when requesting a comparison.

~~~json
{
  "name": "list_segment_efforts",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "segmentId": "33333333-3333-4333-8333-333333333333",
    "filter": {
      "from": "2026-01-01",
      "to": "2026-01-31",
      "minimumElapsedSeconds": 60,
      "maximumElapsedSeconds": 3600,
      "sort": "rank"
    },
    "page": 1,
    "pageSize": 25
  }
}
~~~

#### compare_segment_efforts

Compare two explicit efforts from the same segment and profile. The response includes the elapsed-time delta and at most 100 distance-aligned timing samples without GPS coordinates. The delta is comparison minus baseline elapsed time; a negative value means the comparison effort was faster. Unavailable alignments have an explicit reason.

~~~json
{
  "name": "compare_segment_efforts",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "segmentId": "33333333-3333-4333-8333-333333333333",
    "baselineEffortId": "44444444-4444-4444-8444-444444444444",
    "comparisonEffortId": "55555555-5555-4555-8555-555555555555"
  }
}
~~~

#### search_routes

Find saved cycling routes whose names contain the example search text, ordered by greatest distance.

~~~json
{
  "name": "search_routes",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "search": "Example loop",
    "sport": "cycling",
    "sort": "distance-desc",
    "page": 1,
    "pageSize": 25
  }
}
~~~

#### get_route

Read scalar route measurements and the route description. Geometry and source files are not returned.

~~~json
{
  "name": "get_route",
  "arguments": {
    "profileId": "11111111-1111-4111-8111-111111111111",
    "routeId": "66666666-6666-4666-8666-666666666666"
  }
}
~~~

### Results and availability

Tools advertise input and output schemas. Oversized responses fail explicitly rather than returning cut-off JSON. Dashboard requests exceeding 120 trend buckets are rejected before activity materialization. Use a narrower period or monthly grouping; `search_activities` still provides complete all-time totals.

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
