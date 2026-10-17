# Local testing

Three processes make up the local stack, the same shape as production:

| Piece | Local | Port |
|---|---|---|
| API (`src/Server`) | Docker container or `dotnet run` | 8080 |
| Blazor frontend (`src/WasmApp`) | `dotnet watch run` dev server | 8443 |
| SWA CLI (static hosting, Easy Auth emulator, `/api` proxy via the Functions host) | `swa start rss-reader-local` | 4280 |

Open **http://localhost:4280**. Deep links such as `/timeline` do not work through
the SWA proxy; load `/` and let the app route. Hard-refresh (Ctrl+Shift+R) after a
rebuild, because the Blazor service worker caches aggressively.

Authentication is bypassed locally: the API runs with
`RssAppConfig__IsTestUserEnabled=true`, the proxy with `IS_TEST_USER_ENABLED=true`,
and the Debug build of the frontend ships `appsettings.Development.json` with
`EnableTestAuth: true`. **Never run the frontend from a Release publish locally**: that
file is excluded from publish output, Easy Auth is then enforced, and every API call
returns 401.

## 1. Backend

### Option A: Docker (matches production)

```powershell
docker build -f src/Server/Dockerfile -t rss-reader-api:local .

New-Item -ItemType Directory -Path C:\dev\rssreader\docker-data -Force

docker run -d --name rss-reader-test -p 8080:8080 `
  -v C:\dev\rssreader\docker-data:/data `
  -e RssAppConfig__IsTestUserEnabled=true `
  rss-reader-api:local

docker logs -f rss-reader-test
```

Without `RssAppConfig__BackupDbPath` the container runs in local-dev mode: the database
is created fresh at `/tmp/storage.db` and is not copied to `/data`, so it is lost when
the container is removed. Add
`-e RssAppConfig__BackupDbPath=/data/db/storage.db` to exercise the production seed and
backup path against the mounted folder.

Rebuild after a code change with `docker rm -f rss-reader-test` and the same two
commands. Everything else (`docker ps`, `docker exec -it rss-reader-test /bin/bash`,
`docker stats`) is standard Docker.

### Option B: dotnet run

```powershell
$env:RSSREADER_API_KEY = "local-dev-key"
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/Server/Server.csproj --no-launch-profile --urls http://localhost:8080
```

Faster to iterate on and what `.claude/launch.json` uses. The database is wherever
`RssAppConfig:DbLocation` points in `appsettings.Development.json`.

Either way, confirm with:

```powershell
Invoke-WebRequest http://localhost:8080/api/healthz -UseBasicParsing
```

## 2. Proxy settings

The Functions host needs `api/local.settings.json`, which is gitignored. Create it once
per checkout (and per worktree):

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "node",
    "RSSREADER_API_URL": "http://localhost:8080",
    "RSSREADER_API_KEY": "local-dev-key",
    "IS_TEST_USER_ENABLED": "true"
  }
}
```

Then `cd api && npm ci`.

## 3. Frontend

```powershell
dotnet build -c Debug src/WasmApp/WasmApp.csproj
swa start rss-reader-local
```

`swa start` reads `swa-cli.config.json`, launches `dotnet watch run` for the frontend on
8443, starts the Functions host for `api/`, and serves the whole thing on 4280 with the
Easy Auth emulator. Edits to `.razor` files hot-reload.

### From a git worktree

`swa-cli.config.json` resolves `appLocation` against the directory it is run from, so run
`swa start` from inside the worktree. The `.claude/launch.json` entries in the main
checkout start the four pieces separately (`rss-api`, `rss-func`, `rss-wasm`, `rss-swa`)
and point at a worktree path; update that path when you switch worktrees. Stale
`blazor-devserver` processes can hold 8443 and keep serving an old build; kill them
before restarting.

## 4. Tests

```powershell
dotnet test test/SerializerTests/SerializerTests.csproj
```

The load scenarios under `test/load/` run against a local stack; see
[test/load/REPORT.md](test/load/REPORT.md) for what they measure.

## Stopping

```powershell
docker rm -f rss-reader-test
Get-Process -Name func, blazor-devserver, swa -ErrorAction SilentlyContinue | Stop-Process
```

## Troubleshooting

- **Port 8080 in use:** `netstat -ano | Select-String ":8080"`, then stop that process or
  run the container with `-p 9090:8080` and change `RSSREADER_API_URL` to match.
- **Container stops immediately:** `docker logs rss-reader-test`.
- **Proxy returns 500 "RSSREADER_API_URL not configured":** `api/local.settings.json` is
  missing in this checkout.
- **Every API call is 401:** the frontend is a Release build, or `IS_TEST_USER_ENABLED`
  is not `true` in the proxy settings.
- **Frontend shows an old build:** a leftover `blazor-devserver` on 8443; stop it and
  rerun `swa start`.
