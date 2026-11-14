# Architecture

```
Browser ── static files ──▶ Frontend   src/WasmApp   rss.brandonchastain.com (Static Web Apps)
   │
   └── fetch + session cookie ──▶ API   src/Server   api.rss.brandonchastain.com (Container Apps, 1 replica)
                                         │  SQLite at /tmp/storage.db
                                         └─ Litestream ──▶ Azure Blob (replica, writer lease, cookie keys)
```

## Frontend

Blazor WebAssembly, served as static files. It calls the API at `ApiBaseUrl`
(`wwwroot/appsettings.json`) with the session cookie. The last-seen timeline, tags and
post bodies are cached in `localStorage`, so the page paints at once and then refreshes.
Read/saved toggles that fail are queued and replayed later.

## API

ASP.NET Core with SQLite. Exactly one replica runs, because SQLite has one writer.

- `/auth/*`: sign-in. The API is the OpenID Connect client for Google, Microsoft and
  GitHub, and issues its own encrypted session cookie (`.rss.session`, 30 days sliding).
- `/api/feed`, `/api/item`, `/api/user`, `/api/admin`: the app (`src/Server/Controllers`).
  Every endpoint declares its authorization; `ApiContractTests` enforces it.
- `/api/healthz`, `/api/health/feeds`: anonymous health checks.

A background scheduler refreshes every distinct feed on its own cadence. A retention
service deletes posts older than 180 days.

## Data

The schema lives in `src/Server/Data/DatabaseSchema.cs`. Each post is stored once per
feed (`Posts`, `PostContent`). Per-user state is sparse: a `UserPostState` row exists only
once a user reads or saves a post. Tags hang off a user's `Subscriptions`. Sign-in identities
(`provider:subject`) live in `UserIdentities`.

Schema changes are additive only (`CREATE TABLE/INDEX IF NOT EXISTS`). The database is
live and replicated, and a rollback must still work.

## Durability

There is one copy of the database worth keeping: the Litestream replica in the
`litestream` blob container. The live file is on the container's local disk.

- **Replication.** The app runs `litestream replicate` as a child process
  (`LitestreamSupervisor`). It syncs every 30 minutes and snapshots hourly
  (`infrastructure/litestream.yml`).
- **One writer.** The writer holds a blob lease (`BlobWriterLease`), so two revisions
  never write at once.
- **Deploys lose nothing.** A new revision starts a placeholder listener
  (`WriterStartupGate`), restores the replica into a staging file, and asks the running
  writer to hand over (`CooperativeHandoverSeeder`). The old writer stops background work,
  checkpoints, runs a final sync, and releases the lease (`WriterHandover`). The new one
  catches up, runs `PRAGMA quick_check`, and swaps the file in. API calls get 503s for a
  few seconds.
- **Crashes lose at most 30 minutes.** That is the time since the last sync.
- **No fallback.** If the restore fails, the container restarts and tries again. It
  never starts with an empty database.
- **Sessions survive deploys.** The cookie key ring is the blob `dataprotection/keys.xml`.

## Configuration

Every setting is documented where it is defined:

- `src/Server/Config/RssAppConfig.cs`, `DurabilityConfig.cs`, `AuthConfig.cs`. They are
  bound from `appsettings.json` or `RssAppConfig__<Name>` environment variables.
- Production overrides are the container env block in `infrastructure/main.bicep`. Sizing
  is in `main.bicepparam`.

## Running costs

The always-on replica and blob calls are the bill. The levers are in `main.bicep`:

- `LITESTREAM_SYNC_INTERVAL` (30 min): a shorter interval loses less in a crash. At 30 s
  it cost about $3/month in blob listings.
- Lease and handover poll intervals: a longer interval means fewer blob calls, but a
  slower takeover.
- Each deploy uploads one full copy of the database, so fewer deploys is cheaper.
