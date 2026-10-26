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
|  |  DB replicated to blob by Litestream (30 s)   |   |
|  |  +-----------+                               |   |
|  |  |  SQLite   |                               |   |
|  |  +-----------+                               |   |
|  +----------------------------------------------+   |
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
Litestream blob replica described below. There is no Azure Files share.

##### Backup & Replication

There is **one** durability path: the Litestream replica in the `litestream`
blob container. This section is the only place that describes it; the other
docs link here.

1. **Blob replication.** With `RssAppConfig:Durability:LitestreamEnabled` the
   app starts `litestream replicate` as its child (`LitestreamSupervisor`) once
   `/tmp/storage.db` is in place, replicating it to the `litestream` blob
   container every 30 s through the app's managed identity
   (`infrastructure/litestream.yml`). The entrypoint does nothing but
   `exec dotnet Server.dll` for the writer; Litestream never sees the file
   replaced underneath it.

2. **Writer lease.** With `RssAppConfig:Durability:BlobServiceUri` the writer
   holds a 30 s blob lease on `litestream/writer-lease` (`BlobWriterLease`),
   acquired while the startup gate answers the probe. Every acquire and release
   is logged.

3. **Startup restore and cooperative handover.** In production
   `RssAppConfig:Durability:RestoreFromBlob` is true. A deploy overlaps the old
   and new revision even in `Single` revision mode, and anything written since
   the last replicated WAL exists only in the old replica's `/tmp`. The sequence
   that keeps it:

   1. The new revision starts a throwaway listener (`WriterStartupGate`) that
      answers `/api/healthz` with 200 and everything else with 503, so the
      startup probe passes and Container Apps moves traffic to it and sends the
      old revision SIGTERM.
   2. It starts `litestream restore -f` into a staging file
      (`LitestreamFollower`: full restore, then the LTX tail every 500 ms) while
      the old revision keeps serving; a follower that dies is restarted up to
      three times. Only once the initial restore is on disk does it write a
      request on the `writer-handoff` blob (`CooperativeHandoverSeeder`). Its
      startup probe stays 503 until the database is in place, so traffic stays
      on the old revision; after 30 s without a finished handover the probe
      opens anyway, because an old image that ignores the request only releases
      the lease on SIGTERM, which the platform sends once the probe passes.
   3. The running writer polls that blob every 2 s (`WriterHandoffService`) and,
      on a request, stops the feed scheduler, worker and retention service,
      checkpoints, runs the final Litestream sync, acknowledges with the final
      transaction id and releases the lease (`WriterHandover`); from then on it
      answers 503 to mutating requests and serves reads until the platform
      stops it.
   4. The new writer takes the lease, waits for the follower to reach the
      acknowledged transaction id, stops it, runs `PRAGMA quick_check` and
      renames the copy over `/tmp/storage.db`, then stops the gate listener and
      builds the real app. API requests get 503 (then a few seconds of refused
      connections while the app boots) in between.

   There is no fallback to a copy elsewhere. A failed restore throws, the
   container restarts and tries again; a restore that finds no replica fails
   rather than start with an empty database. A full `litestream restore` after
   the lease is not a fast path: it measured 80 s when the replica chain was
   4.8 GB. `/api/healthz` reports row counts so a restore of the replica can be
   compared with the live writer by hand.

   **Shutdown.** Hosted services stop in reverse registration order: the feed
   scheduler and worker stop, the database is checkpointed
   (`wal_checkpoint`), `litestream sync -wait` uploads the pending WAL
   (`FinalSyncTimeout`, 20 s) and the child stops, then the lease is released.
   `HostOptions.ShutdownTimeout` is 50 s and `terminationGracePeriodSeconds` 60.
   A crash loses at most the writes since the last 30 s sync.

   **First environment.** A brand-new environment has no replica to restore. Boot
   once with `RssAppConfig__Durability__RestoreFromBlob=false`: that creates the
   database and uploads the first replica; then turn restore back on.

   **Session keys and images.** The cookie key ring is the blob
   `dataprotection/keys.xml` (`RssAppConfig:Auth:DataProtectionKeysContainer`,
   managed identity), so deploys keep sessions. Cached favicons live only on the
   container disk (`wwwroot/images/`) and are re-fetched after a deploy.

   Measured so far (2026-10-24 to 25, `/api/user` probed once a second; the
   share and lock wait mentioned in the early rows no longer exist):

   | Deploy | 503 window | Why |
   |---|---|---|
   | Before the cooperative handover (PR #178) | about 61 s | Platform SIGTERM 31 s after the probe passes, old shutdown 8 s, share lock wait 17 s, seed 7 s |
   | First handover deploy (#179) | about 117 s | Follower died racing the old writer's compaction; fallback was an 80 s full restore of a 4.8 GB chain; probe opened at once |
   | After #184 (follower retry, request after restore, probe held) | no 5xx in the CI probe | Initial restore 17 s overlapped the old revision; old writer released the lease 2.2 s after the request; restore applied in 25 s |
   | First deploy on the fresh `storage-v2.db` replica (#186) | about 46 s | Expected: no replica yet, follower failed three times, startup seeded from the share |

   `ReplicaChainReporter` logs, a minute after start and every ten minutes,
   `Replica chain at <utc>: L0 n files X MB txid a-b newest hh:mm:ss; L1 ...; L9 ...`
   and `A restore would read N files, X MB`. Read it as the restore bill: a
   healthy chain is one snapshot (L9) plus a short tail, so the plan is a few
   hundred MB at most. Levels 2 and 3 should hold files after 5 minutes and 1
   hour; each level briefly holds a copy of the initial full-database file
   until compaction trims the lower levels. The hourly snapshot and 3 h
   retention in `infrastructure/litestream.yml` bound the chain: everything
   older than the oldest kept snapshot is deleted (the newest snapshot is always
   kept), so the replica holds about three snapshots plus the tail since the
   last deleted one (about 0.9 to 1.4 GB at 326 MB).
   After every restart Litestream uploads one full-database file, so the chain
   grows by about one database per restart until retention trims it. The
   point-in-time window is therefore three to four hours.


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
| `staticWebAppSku` | `Free` | — | SWA tier. |
| `customDomain` | `rss.brandonchastain.com` | — | SWA custom domain. |
| `apiCustomDomain` | `api.rss.brandonchastain.com` | — | The API's own hostname, so a session cookie can be shared with the frontend origin. Empty removes the binding. |
| `googleClientId`/`Secret`, `microsoftClientId`/`Secret`, `githubClientId`/`Secret` | empty | from repo secrets | Login providers. A missing pair means that provider is not offered. |
| `terminationGracePeriodSeconds` (inline) | `60` s | — | How long a stopping replica gets before it is killed. Must exceed the app's shutdown budget (`HostOptions.ShutdownTimeout` 50 s, which contains the 20 s `FinalSyncTimeout`) or the final Litestream sync on deploy is cut off. |
| `location`, `*Name` params | westus2, `rss-reader-*` | — | Resource naming and region. |

### Container environment (writer env block in `main.bicep`)

| Variable | Prod | Effect |
|---|---|---|
| `RssAppConfig__Durability__*` | `LitestreamEnabled`, `BlobServiceUri`, `RestoreFromBlob=true` | Blob replication, the writer lease and the startup restore. Any `RssAppConfig__*` setting can be overridden the same way. |
| `Logging__LogLevel__Default` | `Warning` | Log volume into Log Analytics. |
| `RssAppConfig__Auth__DataProtectionKeysContainer` | `dataprotection` | Blob container holding the session-cookie key ring (`keys.xml`), so deploys keep sessions. |
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
| `SchedulerFetchesPerSecond` | 10 | Budget: the scheduler starts at most this many fetches a second, spreading due feeds over time instead of bursting (about 36,000 distinct feeds fit in the one-hour default interval). Manual refreshes are not paced. 0 disables. A yield-to-live-traffic step was measured and dropped (test/load/REPORT.md, "Refresh pacing"). |
| `FeedBackoffBase` / `FeedBackoffMax` | 2 min / 6 h | Exponential backoff after a failed fetch (base × 2^(failures−1), jittered). `Retry-After` wins. In-memory only. |
| `BackgroundWorkerCount` | 1 | Keep at 1: more workers contend for SQLite's single writer lock. |
| `BackgroundQueueCapacity` | 1000 | Bounded queue of background jobs; producers wait when it is full. |

### Caches and startup

| Setting | Default | Effect |
|---|---|---|
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
| `Providers:<google|microsoft|github>:ClientId/ClientSecret/Authority` | empty | A provider is offered when both id and secret are set. `Authority` defaults to Google's public endpoint and Microsoft's `consumers` endpoint (personal accounts only). |
| `AllowedFrontendOrigins` | empty | Origins allowed to call with credentials and to be a login/logout `returnUrl`. The first is the default return target. Required. |
| `CookieName` / `CookieDomain` / `CookieSameSite` | `.rss.session` / empty / `None` | Session cookie. `None` implies `Secure`; Development uses `Lax`. |
| `SessionLifetime` | 30 d | Sliding session expiry. |
| `LoginRateLimitPerMinute` / `AddFeedRateLimitPerMinute` | 30 / 30 | Login and logout requests per client address, and feed additions and OPML imports per user, per minute. |
| `DataProtectionKeysContainer` / `DataProtectionKeysPath` | empty | Blob container (managed identity) or directory for the cookie encryption keys. The container wins; with both empty the keys stay in the container, which signs everyone out on restart. The directory form is for local and self-hosted use. |

| Setting | Default | Effect |
|---|---|---|
| `IsTestUserEnabled` | `false` | Every request is the test user; no provider needed. Refused in Production unless `AllowTestUserInProduction`. |
| `AllowTestUserInProduction` | `false` | Lets `IsTestUserEnabled` run in Production, for a private single-user box with no login. |
| `AdminAadUserIds` | empty | Comma-separated login ids (`provider:subject`, or a bare subject) that may use `/admin`. |
| `DbLocation` | — | Path of the live SQLite file (`/tmp/storage.db` in prod). Self-hosted without Azure, point it at a mounted volume; with no `Durability` settings SQLite just opens the file. |

### Hardcoded (for now)

| Constant | Value | Where |
|---|---|---|
| Handover request poll interval | 2 s | `WriterHandoffService` |
| Writer lease duration | 30 s | `BlobWriterLease` |
| Host shutdown timeout | 50 s | `Program.cs` (`HostOptions.ShutdownTimeout`) |
| Outbound feed HTTP timeout | 20 s | `Program.cs` (`RssClient`) |
| In-memory feed/item cache | 5 min sliding | `CachingFeedRepository`, `CachingItemRepository` |
| In-memory post body cache | 1 h absolute | `CachingItemRepository.ContentExpiration` |
| Content batch size | 50 ids | `ItemController.MaxContentBatchSize` |
| Page size ceiling | 500 | `FeedClient` clamps `pageSize` before calling feed / search endpoints |
| Litestream `sync-interval` / `snapshot-interval` | 30 s / 1 h | `infrastructure/litestream.yml` |

## Frontend (`src/WasmApp`)

`RssWasmConfig` in `wwwroot/appsettings.json`: `ApiBaseUrl` (the API origin,
`https://api.rss.brandonchastain.com/` in prod; empty means the page's own origin,
for when the API serves the frontend). Login,
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
