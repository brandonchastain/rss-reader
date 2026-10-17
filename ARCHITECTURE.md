# Diagram

```
+-----------------------------------------------------+
|                RSS Reader                           |
|  +----------------------------------------------+   |
|  |  Frontend - /src/WasmApp/  (static files)    |   |
|  |  rss.example.com, browser cache of timeline  |   |
|  +----------------------------------------------+   |
|        |  fetch + session cookie    ^ login/logout   |
|        v                            | redirects      |
|  +----------------------------------------------+   |
|  |  API - /src/Server/   api.rss.example.com    |   |
|  |  /auth/*  cookie session; OIDC to            |   |
|  |           Google / Microsoft / GitHub        |   |
|  |  /api/*   feeds, items, users, admin         |   |
|  |  Writer (min 1, max 1)                       |   |
|  |  DB copied to /data every 5 min              |   |
|  |  +-----------+                               |   |
|  |  |  SQLite   |                               |   |
|  |  +-----------+                               |   |
|  +----------------------------------------------+   |
|  (optional read replicas: see DEPLOY.md)            |
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
   mount (`/data/db/storage.db`) every `BackupInterval` (skipped when the DB
   has not changed), and once more on shutdown. The writer holds a lock file
   next to the copy (`storage.db.lock`) for its whole lifetime, refreshing its
   mtime every 30 s; only the lock holder copies. On boot the app (not the
   entrypoint) seeds `/tmp/storage.db` from the share copy, after validating it
   with `PRAGMA quick_check`; a failed check stops the app.
   `DatabaseBackupService` does the same for cached images (`wwwroot/images/`
   to `/data/images/`, every 5 minutes) and records system-stats snapshots.
   Backing up `/data` backs up everything.

   **Deploy handover.** A deploy overlaps the old and new revision even in
   `Single` revision mode, and anything written since the last periodic copy
   exists only in the old replica's `/tmp`. The sequence that keeps it:

   1. The new revision starts a throwaway listener (`WriterStartupGate`) that
      answers `/api/healthz` with 200 and everything else with 503, and waits
      while the old revision's lock is alive.
   2. The startup probe passes, so Container Apps moves traffic to the new
      revision and sends the old one SIGTERM.
   3. The old revision stops Kestrel and the feed scheduler, then
      `DatabaseBackupToFileService.StopAsync` runs one final copy (ignoring the
      unchanged-DB skip, bounded by `ShutdownBackupTimeout`) and releases the
      lock. `terminationGracePeriodSeconds` (120) covers this.
   4. The new revision sees the lock gone, takes it, seeds `/tmp` from the
      fresh copy, stops the gate listener and builds the real app. API requests
      get 503 (then a few seconds of refused connections while the app boots)
      between steps 2 and 4.

   A lock whose heartbeat is older than `BackupLockStaleAfter` (3 min) belongs
   to a replica that died without releasing it and is taken over, so a crash
   delays the next boot by at most that long. Before publishing a copy the
   holder re-reads the lock's owner token and discards the copy if another
   writer took over. Scaling mode (below) still seeds in the entrypoint and has
   no handover.

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

Routing reads to the reader used to be the job of the Static Web Apps Functions proxy, which the login migration removed. Nothing routes to readers today: the frontend calls the writer's hostname directly. Turning `enableReadReplica=true` back on needs a router in front of both apps (or the frontend sending read-only GETs to a second base URL) that falls back to the writer on any error. Until that exists the parameter stays `false`.


# Knobs

Everything below can be changed without touching code paths, grouped by where
it is set. "Prod" is the value `infrastructure/main.bicepparam` or the
container's env block deploys; blank means the code default applies.

## Infrastructure (`infrastructure/main.bicep`, values in `main.bicepparam`)

Deployed by the Deploy workflow on every merge to `main`. `containerImage` and
the provider secrets are supplied by the workflow, not the param file.

| Parameter | Default | Prod | Effect |
|---|---|---|---|
| `cpuCore` | `1.0` | `0.5` | vCPU per writer replica. Billed per second, active or idle. |
| `memorySize` | `1.0Gi` | `1.0Gi` | Memory per writer replica. Idle memory is the larger half of the always-on bill. |
| `minReplicas` | `0` | `1` | `0` scales the writer to zero after `cooldownPeriod` (45s cold start). `1` keeps one warm (~$15/month idle at 0.5 vCPU / 1 GiB). |
| `maxReplicas` | `1` | `1` | Must stay `1`: SQLite has one writer. |
| `cooldownPeriod` (inline) | `900` s | — | How long KEDA waits before scaling an idle replica away. Keep above boot time or activation fails mid-boot. |
| `http-rule concurrentRequests` (inline) | `10` | — | Concurrent requests per replica before KEDA scales out (moot while `maxReplicas` is 1). |
| Startup probe (inline) | `2s` period × `150` failures | — | 300s startup budget against `/api/healthz`. |
| `enableReadReplica` | `false` | — | Deploys the reader Container App. Needs Litestream replication on. |
| `maxReadReplicas` | `3` | — | Reader scale ceiling (reader is 0.25 vCPU / 0.5 GiB, min 0). |
| `staticWebAppSku` | `Free` | — | SWA tier. |
| `customDomain` | `rss.brandonchastain.com` | — | SWA custom domain. |
| `apiCustomDomain` | `api.rss.brandonchastain.com` | — | The API's own hostname, so a session cookie can be shared with the frontend origin. Empty removes the binding. |
| `googleClientId`/`Secret`, `microsoftClientId`/`Secret`, `githubClientId`/`Secret` | empty | from repo secrets | Login providers. A missing pair means that provider is not offered. |
| `shareQuota` (inline) | `5` GB | — | Azure Files share for `/data`. Must exceed 2× the DB size because a backup cycle briefly holds old and new copies. Billed by used bytes. |
| `terminationGracePeriodSeconds` (inline) | `120` s | — | How long a stopping replica gets before it is killed. Must exceed the app's shutdown budget (`HostOptions.ShutdownTimeout` 110 s, which contains `ShutdownBackupTimeout`) or the final DB copy on deploy is cut off. |
| `location`, `*Name` params | westus2, `rss-reader-*` | — | Resource naming and region. |

### Container environment (writer env block in `main.bicep`)

| Variable | Prod | Effect |
|---|---|---|
| `RSSREADER_ENABLE_LITESTREAM_REPLICATION` | `false` | `true` runs the writer under `litestream replicate` (scaling mode). Requires `BackupDbPath`. |
| `RssAppConfig__BackupDbPath` | `/data/db/storage.db` | Where the DB copy lives. Empty disables backup entirely (local dev). Any `RssAppConfig__*` setting can be overridden the same way. |
| `Logging__LogLevel__Default` | `Warning` | Log volume into Log Analytics. |
| `APP_ROLE` | `writer` | `reader` selects the read-only boot path in `docker-entrypoint.sh`. |
| `RssAppConfig__Auth__DataProtectionKeysPath` | `/data/keys` | Session-cookie encryption keys, on the mount so deploys keep sessions. |
| `RssAppConfig__Auth__Providers__<name>__ClientId` / `ClientSecret` | secrets | One pair per offered provider. |
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
| `BackupInterval` | 15 min | How often the whole DB file is copied to the share when it changed. Each copy is ~1 Azure Files write op per MB. A final copy also runs on shutdown, so this is the crash-loss window, not the deploy-loss window. |
| `BackupLockStaleAfter` | 3 min | A writer lock whose heartbeat (every 30 s) is older than this is taken over. Worst-case boot delay after a crashed replica; keep a small multiple of the heartbeat and above any SMB hiccup. |
| `ShutdownBackupTimeout` | 90 s | Upper bound for the final DB copy on SIGTERM. Must fit inside `HostOptions.ShutdownTimeout` (110 s) and `terminationGracePeriodSeconds`. |
| `CacheReloadInterval` | 5 min | Cooldown between one user's manual refreshes; inside it the refresh endpoint reports "up to date" without fetching. |
| `CacheReloadStartupDelay` | 0 s | Grace period after boot during which user refreshes are served from cache. |
| `RebuildFtsOnStartup` | `false` | Rebuild the full-text index at boot (slow; use after a schema change). |

### Identity and roles

The API is the OpenID Connect relying party. `GET /auth/login/{provider}` sends the
browser to the provider; the callback signs a session cookie; `GET /auth/me` returns
`{ clientPrincipal: { identityProvider, userId, userDetails, userRoles } }` or
`{ clientPrincipal: null }`; `GET /auth/logout` clears the cookie. `userId` is
`provider:subject` (GitHub id, Google `sub`, Entra `oid`), and `Users.AadUserId`
holds it. Rows that pre-date API login hold a Static Web Apps id there and their
provider id in `Users.ProviderUserId`; the resolver looks in both.

| Setting (`Auth:`) | Default | Effect |
|---|---|---|
| `Providers:<google|microsoft|github>:ClientId/ClientSecret/Authority` | empty | A provider is offered when both id and secret are set. `Authority` defaults to the public Google and Microsoft `common` endpoints. |
| `AllowedFrontendOrigins` | empty | Origins allowed to call with credentials and to be a login/logout `returnUrl`. The first is the default return target. Required. |
| `CookieName` / `CookieDomain` / `CookieSameSite` | `.rss.session` / empty / `None` | Session cookie. `None` implies `Secure`; Development uses `Lax`. |
| `SessionLifetime` | 30 d | Sliding session expiry. |
| `DataProtectionKeysPath` | empty | Directory for the cookie encryption keys; empty means in-container, which signs everyone out on restart. |
| `EnableStaticWebAppsHeaders` | `true` | Migration only: also accept the Static Web Apps proxy's gateway header and principal. Set `false` after the cutover. |

| Setting | Default | Effect |
|---|---|---|
| `IsReadOnly` | `false` | Reader mode: no background services, writes rejected. |
| `IsTestUserEnabled` | `false` | Every request is the test user; no provider needed (dev only). |
| `AdminAadUserIds` | empty | Comma-separated user ids (either stored id) that may use `/admin`. |
| `ServerHostName` | localhost | Public base URL the API reports for itself. |
| `DbLocation` | — | Path of the live SQLite file (`/tmp/storage.db` in prod). |

### Hardcoded (for now)

| Constant | Value | Where |
|---|---|---|
| Image cache sync interval | 5 min | `DatabaseBackupService._syncInterval` |
| Backup lock heartbeat | 30 s | `DatabaseBackupToFilePaths.HeartbeatInterval` |
| Lock wait poll interval | 2 s | `ShareWriterLock.PollInterval` |
| Host shutdown timeout | 110 s | `Program.cs` (`HostOptions.ShutdownTimeout`) |
| Outbound feed HTTP timeout | 20 s | `Program.cs` (`RssClient`) |
| In-memory feed/item cache | 5 min sliding | `CachingFeedRepository`, `CachingItemRepository` |
| In-memory post body cache | 1 h absolute | `CachingItemRepository.ContentExpiration` |
| Content batch size | 50 ids | `ItemController.MaxContentBatchSize` |
| Page size ceiling | 500 | `FeedClient` clamps `pageSize` before calling feed / search endpoints |
| Litestream `sync-interval` / `snapshot-interval` | 30 s / 1 h | `infrastructure/litestream.yml` (scaling mode only) |

## Frontend (`src/WasmApp`)

`RssWasmConfig` in `wwwroot/appsettings.json`: `ApiBaseUrl` (the API origin,
`https://api.rss.brandonchastain.com/` in prod; empty means the page's own origin,
for when the API serves the frontend), `EnableTestAuth`, `TestAuthUsername`. Login,
logout and `/auth/me` all go to `{ApiBaseUrl}auth/*`; API calls send the session
cookie.

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
