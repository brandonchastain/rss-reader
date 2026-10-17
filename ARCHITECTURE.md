# Diagram

```
+-----------------------------------------------------+
|                RSS Reader                           |
|  +----------------------------------------------+   |
|  |  Frontend Web App - /src/WasmApp/            |   |
|  |  (browser cache of the last timeline)        |   |
|  +----------------------------------------------+   |
|                 |   HTTP                            |
|                 v                                   |
|  +------------------------------------------+       |
|  | Azure Function API Proxy - /api/         |       |
|  | Routes reads → reader, writes → writer   |       |
|  +------------------------------------------+       |
|           |                         |               |
|     WRITES (POST,etc)         READS (GET)           |
|           v                         v               |
|  +--------------------+  +--------------------+     |
|  | Writer (min 1,     |  | Reader (0→N,       |     |
|  |         max 1)     |  |  optional)         |     |
|  | /src/Server/       |  | /src/Server/       |     |
|  | DB copied to       |  | Read-only mode     |     |
|  |  /data every 5 min |  | Litestream restore |     |
|  |  (Azure Files)     |  |  (startup only)    |     |
|  |  +-----------+     |  |  +-----------+     |     |
|  |  |  SQLite   |     |  |  |  SQLite   |     |     |
|  |  | (R/W)     |     |  |  | (R/O app) |     |     |
|  |  +-----------+     |  |  +-----------+     |     |
|  +--------------------+  +--------------------+     |
+-----------------------------------------------------+
```




## Frontend Web App

A static web app that runs on WASM, written in C# with Blazor.

#### Stack

C#, Blazor Webassembly, Javascript, HTML, CSS

#### Browser cache

The last-seen timeline (one slot per filter), the user record, the tag list and
post bodies are kept in `localStorage` (`src/WasmApp/Services/PostCache.cs`,
`wwwroot/app.js`). On open the cached rows paint immediately, then the newest
page is fetched and swapped in; the cache is what stays on screen only when that
fetch fails. Read/saved toggles that the API rejects are queued in an outbox and
replayed on the next successful probe.


## Backend Web API

An ASP.NET Core Web API that enables the frontend to update server state for users, feeds, and posts.

#### Stack

C#, ASP.NET, SQLite

#### Endpoints (C# controllers)
* `/api/feed/*` - Retrieve, refresh, and import/export RSS feeds
* `/api/item/*` - Retrieve, search, and save RSS posts
* `/api/user/*` - Retrieve user info and login/register

#### Database
SQLite database to store RSS feeds, posts, and user profiles. The live file is
`/tmp/storage.db` on the container's local disk; durability comes from the
services below.

##### Backup & Replication

There is **one** durability path, plus a legacy bootstrap and an optional
replication mode. This section is the only place that describes them; the other
docs link here.

1. **File-mount copy (the durability path).** `DatabaseBackupToFileService`
   copies the live database to `RssAppConfig.BackupDbPath` on the Azure Files
   mount (`/data/db/storage.db`) every `BackupInterval`, under a lock file so
   only one writer copies at a time. `docker-entrypoint.sh` seeds
   `/tmp/storage.db` from that file on every boot and runs `PRAGMA quick_check`
   on the copy concurrently with app startup, stopping the app if it fails.
   `DatabaseBackupService` does the same for cached images (`wwwroot/images/`
   to `/data/images/`, every 5 minutes) and records system-stats snapshots.
   Backing up `/data` backs up everything.

2. **Litestream bootstrap (legacy, read-only).** If `BackupDbPath` does not
   exist at boot, the entrypoint tries a one-shot `litestream restore` from the
   `litestream` blob container that the previous architecture replicated to,
   validates it and publishes it as the first file-mount copy. Nothing writes to
   that blob container in the default mode.

3. **Litestream replication (scaling mode, off).** With
   `RSSREADER_ENABLE_LITESTREAM_REPLICATION=true` the writer runs under
   `litestream replicate`, streaming WAL changes to blob storage so read
   replicas can restore from it. It is a feed for readers, not a second backup.
   Config: `infrastructure/litestream.yml`.

##### Read Replicas (optional)

The backend supports an optional read replica mode controlled by `RssAppConfig.IsReadOnly` and the `APP_ROLE` environment variable:

- **Writer** (`APP_ROLE=writer`, default) — Single replica, all background services (feed scheduler, backups, image sync), handles reads and writes.
- **Reader** (`APP_ROLE=reader`, `IsReadOnly=true`) — 0→N replicas, restores from the Litestream blob on startup, serves read-only traffic. No background services. Uses `NoOpFeedRefresher` so controllers still resolve.

The Azure Functions proxy (`api/src/functions/ApiProxy.js`) routes GET requests for read-heavy endpoints (timeline, feed list, search, content) to the reader when `RSSREADER_READER_API_URL` is configured. All write endpoints and the `user` endpoint always go to the writer. If the reader is unavailable (5xx or network error), the proxy falls back to the writer.

Enable via Bicep parameter: `enableReadReplica=true` (and turn on Litestream replication, above).


# Knobs

Everything below can be changed without touching code paths, grouped by where
it is set. "Prod" is the value `infrastructure/main.bicepparam` or the
container's env block deploys; blank means the code default applies.

## Infrastructure (`infrastructure/main.bicep`, values in `main.bicepparam`)

Deployed by the Deploy workflow on every merge to `main`. `gatewaySecretKey`
and `containerImage` are supplied by the workflow, not the param file.

| Parameter | Default | Prod | Effect |
|---|---|---|---|
| `cpuCore` | `1.0` | `0.5` | vCPU per writer replica. Billed per second, active or idle. |
| `memorySize` | `1.0Gi` | `1.0Gi` | Memory per writer replica. Idle memory is the larger half of the always-on bill. |
| `minReplicas` | `0` | `1` | `0` scales the writer to zero after `cooldownPeriod` (45s cold start, past the SWA proxy timeout). `1` keeps one warm (~$15/month idle at 0.5 vCPU / 1 GiB). |
| `maxReplicas` | `1` | `1` | Must stay `1`: SQLite has one writer. |
| `cooldownPeriod` (inline) | `900` s | — | How long KEDA waits before scaling an idle replica away. Keep above boot time or activation fails mid-boot. |
| `http-rule concurrentRequests` (inline) | `10` | — | Concurrent requests per replica before KEDA scales out (moot while `maxReplicas` is 1). |
| Startup probe (inline) | `2s` period × `150` failures | — | 300s startup budget against `/api/healthz`. |
| `enableReadReplica` | `false` | — | Deploys the reader Container App. Needs Litestream replication on. |
| `maxReadReplicas` | `3` | — | Reader scale ceiling (reader is 0.25 vCPU / 0.5 GiB, min 0). |
| `staticWebAppSku` | `Free` | — | SWA tier. |
| `customDomain` | `rss.brandonchastain.com` | — | SWA custom domain. |
| `apiCustomDomain` | `api.rss.brandonchastain.com` | — | The API's own hostname, so a session cookie can be shared with the frontend origin. Empty removes the binding. |
| `shareQuota` (inline) | `5` GB | — | Azure Files share for `/data`. Must exceed 2× the DB size because a backup cycle briefly holds old and new copies. Billed by used bytes. |
| `location`, `*Name` params | westus2, `rss-reader-*` | — | Resource naming and region. |

### Container environment (writer env block in `main.bicep`)

| Variable | Prod | Effect |
|---|---|---|
| `RSSREADER_ENABLE_LITESTREAM_REPLICATION` | `false` | `true` runs the writer under `litestream replicate` (scaling mode). Requires `BackupDbPath`. |
| `RssAppConfig__BackupDbPath` | `/data/db/storage.db` | Where the DB copy lives. Empty disables backup entirely (local dev). Any `RssAppConfig__*` setting can be overridden the same way. |
| `Logging__LogLevel__Default` | `Warning` | Log volume into Log Analytics. |
| `APP_ROLE` | `writer` | `reader` selects the read-only boot path in `docker-entrypoint.sh`. |
| `RSSREADER_API_KEY` | secret | Shared key the proxy must present. |
| `LITESTREAM_AZURE_ACCOUNT_NAME` | storage account | Blob account for Litestream. |

## Server (`RssAppConfig`, `src/Server/Config/RssAppConfig.cs`)

Bound from `appsettings.json` section `RssAppConfig` or env vars
`RssAppConfig__<Name>`. TimeSpans use `hh:mm:ss`.

### Feed scheduler

| Setting | Default | Effect |
|---|---|---|
| `SchedulerEnabled` | `true` | Background fetch of every distinct feed URL on a cadence. `false` falls back to user-triggered refreshes only. |
| `SchedulerTickInterval` | 1 min | How often the scheduler looks for feeds that are due. Each tick is CPU activity, which bills at the active rate. |
| `SchedulerStartupDelay` | 45 s | Hold-off after boot so the first user request is not competing with a fan-out. |
| `FeedRefreshInterval` | 30 min | Poll interval for feeds that advertise no `<ttl>` / `sy:updatePeriod`. |
| `FeedRefreshIntervalFloor` | 15 min | Never poll a feed more often than this, whatever it advertises. |
| `FeedRefreshIntervalMax` | 6 h | Never wait longer than this. |
| `RefreshFetchConcurrency` | 8 | Feeds fetched and parsed concurrently inside one refresh. DB writes stay serialized. |
| `MaxConcurrentFetchesPerHost` | 2 | In-flight fetches to one origin host across all refreshes. |
| `FeedBackoffBase` / `FeedBackoffMax` | 2 min / 6 h | Exponential backoff after a failed fetch (base × 2^(failures−1), jittered). `Retry-After` wins. In-memory only. |
| `BackgroundWorkerCount` | 1 | Keep at 1: more workers contend for SQLite's single writer lock. |
| `BackgroundQueueCapacity` | 1000 | Bounded queue of background jobs; producers wait when it is full. |

### Durability and caches

| Setting | Default | Effect |
|---|---|---|
| `BackupDbPath` | empty | DB copy target. Empty disables `DatabaseBackupToFileService`. |
| `BackupInterval` | 5 min | How often the whole DB file is copied to the share. Each copy is ~1 Azure Files write op per MB, all day long when the replica is always on. |
| `CacheReloadInterval` | 5 min | Cooldown between one user's manual refreshes; inside it the refresh endpoint reports "up to date" without fetching. |
| `CacheReloadStartupDelay` | 0 s | Grace period after boot during which user refreshes are served from cache. |
| `RebuildFtsOnStartup` | `false` | Rebuild the full-text index at boot (slow; use after a schema change). |

### Identity and roles

| Setting | Default | Effect |
|---|---|---|
| `IsReadOnly` | `false` | Reader mode: no background services, writes rejected. |
| `IsTestUserEnabled` | `false` | Accept the local test identity instead of Easy Auth. |
| `AdminAadUserIds` | empty | Comma-separated AAD object ids that may use `/admin`. |
| `ServerHostName` | localhost | Public base URL the API reports for itself. |
| `DbLocation` | — | Path of the live SQLite file (`/tmp/storage.db` in prod). |

### Hardcoded (for now)

| Constant | Value | Where |
|---|---|---|
| Image cache sync interval | 5 min | `DatabaseBackupService._syncInterval` |
| Backup lock stale threshold | 15 min | `Program.cs` (`DatabaseBackupToFilePaths`) |
| Outbound feed HTTP timeout | 20 s | `Program.cs` (`RssClient`) |
| In-memory feed/item cache | 5 min sliding | `CachingFeedRepository`, `CachingItemRepository` |
| In-memory post body cache | 1 h absolute | `CachingItemRepository.ContentExpiration` |
| Content batch size | 50 ids | `ItemController.MaxContentBatchSize` |
| Page size ceiling | 500 | `FeedClient` clamps `pageSize` before calling feed / search endpoints |
| Litestream `sync-interval` / `snapshot-interval` | 30 s / 1 h | `infrastructure/litestream.yml` (scaling mode only) |

## API proxy (`api/`, Azure Functions)

Set as SWA application settings in prod, `api/local.settings.json` locally.

| Variable | Effect |
|---|---|
| `RSSREADER_API_URL` | Writer base URL. Required. |
| `RSSREADER_READER_API_URL` | Reader base URL. When set, read-heavy GETs go here with fallback to the writer. |
| `RSSREADER_API_KEY` | Key forwarded to the backend. Required. |
| `IS_TEST_USER_ENABLED` | `true` injects the local test identity (dev only). |

## Frontend (`src/WasmApp`)

`RssWasmConfig` in `wwwroot/appsettings.json`: `ApiBaseUrl`, `AuthApiBaseUrl`
(empty in prod so calls stay same-origin through the SWA proxy),
`EnableTestAuth`, `TestAuthUsername`.

Hardcoded (for now):

| Constant | Value | Where | Effect |
|---|---|---|---|
| `ProbePolling.IntervalMs` | 30 s | `Services/ProbePolling.cs` | New-posts probe cadence while the tab is visible. |
| `ProbePolling.InitialDelayMs` | 1 s | same | First probe after open; backs off ×2 on failure up to the interval. |
| `PostCache.MaxTimelineItems` | 50 | `Services/PostCache.cs` | Rows persisted per filter slot and size of the reconcile fetch. |
| `MAX_TIMELINE_SLOTS` | 6 | `wwwroot/app.js` | Filter views kept in `localStorage` before the oldest is evicted. |
| `MAX_ITEM_BYTES` / `MAX_CONTENT_BYTES` | 64 KB / 2 MB | `wwwroot/app.js` | Per-body and total cap on cached post bodies. |
| `PendingWrites.MaxEntries` / `MaxAge` | 500 / 14 days | `Services/PendingWrites.cs` | Outbox size and age before queued read/saved toggles are dropped. |
| Default page size | 20 | `Pages/Base/PostPageBase.razor` | Rows per scroll page. |

`staticwebapp.config.json` sets the SWA routing: app routes rewrite to
`index.html` with `no-store`, `_framework/*` assets are immutable for a year
except the boot manifest and loader, which are `no-cache`.
