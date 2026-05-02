# Diagram

```
+-----------------------------------------------------+
|                RSS Reader                           |
|  +----------------------------------------------+   |
|  |  Frontend Web App - /src/WasmApp/            |   |
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
|  | Writer (max: 1)    |  | Reader (0→N)       |     |
|  | /src/Server/       |  | /src/Server/       |     |
|  | Litestream         |  | Read-only mode     |     |
|  |  replicate ──WAL──>|  | Litestream restore |     |
|  |              blob  |  |  (startup only)    |     |
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


## Backend Web API

An ASP.NET Core Web API that enables the frontend to update server state for users, feeds, and posts.

#### Stack

C#, ASP.NET, SQLite

#### Endpoints (C# controllers)
* `/api/feed/*` - Retrieve, refresh, and import/export RSS feeds
* `/api/item/*` - Retrieve, search, and save RSS posts
* `/api/user/*` - Retrieve user info and login/register

#### Database
SQLite database to store RSS feeds, posts, and user profiles.

##### Backup & Replication
The database uses a dual backup strategy:

1. **Litestream** (primary) — Continuously replicates SQLite WAL changes to Azure Blob Storage. On container startup, `docker-entrypoint.sh` runs `litestream restore` to recover the latest state, then starts the app under `litestream replicate` supervision. If Litestream fails (auth error, misconfiguration), the entrypoint falls back to running the app directly. Config: `infrastructure/litestream.yml`.

2. **DatabaseBackupService** (complementary) — Periodically copies the SQLite database to Azure Files every 5 minutes and syncs cached images between `wwwroot/images/` and `/data/images/`. Provides a secondary backup layer alongside Litestream. On startup, if Litestream has already restored the database, `DatabaseBackupService` skips its own DB restore but still restores cached images from Azure Files.

##### Read Replicas (optional)

The backend supports an optional read replica mode controlled by `RssAppConfig.IsReadOnly` and the `APP_ROLE` environment variable:

- **Writer** (`APP_ROLE=writer`, default) — Single replica, runs Litestream replication, all background services (feed refresh, backup), handles reads and writes.
- **Reader** (`APP_ROLE=reader`, `IsReadOnly=true`) — 0→N replicas, restores from Litestream blob on startup, serves read-only traffic. No background services (no feed refresh, no backup). Uses `NoOpFeedRefresher` so controllers still resolve.

The Azure Functions proxy (`api/src/functions/ApiProxy.js`) routes GET requests for read-heavy endpoints (timeline, feed list, search, content) to the reader when `RSSREADER_READER_API_URL` is configured. All write endpoints and the `user` endpoint always go to the writer. If the reader is unavailable (5xx or network error), the proxy falls back to the writer.

Enable via Bicep parameter: `enableReadReplica=true`.

