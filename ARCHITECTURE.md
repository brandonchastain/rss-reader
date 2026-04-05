# Diagram

```
+-----------------------------------------------------+
|                RSS Reader                           |
|  +----------------------------------------------+   |
|  |  Frontend Web App - /src/WasmApp/            |   |
|  +----------------------------------------------+   |
|                 |   HTTP                            |
|                 |                                   |
|                 | /api/feed/*                       |
|                 | /api/item/*                       |
|                 | /api/user/*                       |
|                 v                                   |
|  +------------------------------------------+       |
|  | Azure Function API Proxy - /api/         |       |
|  +------------------------------------------+       |
|                 |   HTTP - forwarded                |
|                 |                                   |
|                 | /api/feed/*                       |
|                 | /api/item/*                       |
|                 | /api/user/*                       |
|                 v                                   |
|  +--------------------------------------------+     |
|  |  RSS Reader Backend Web API - /src/Server/ |     |
|  |                                            |     |
|  |        |                                   |     |
|  |        v                                   |     |
|  |    +-----------+                           |     |
|  |    |  SQLite   |                           |     |
|  |    | Database  |                           |     |
|  |    +-----------+                           |     |
|  +--------------------------------------------+     |
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

2. **DatabaseBackupService** (safety net) — Legacy backup that periodically copies the SQLite database to Azure Files every 5 minutes. Runs in parallel with Litestream during the migration period. If Litestream has already restored the database on boot, `DatabaseBackupService` skips its own DB restore (active DB already exists) but still restores cached images from Azure Files.

