# Local testing

Two processes make up the local stack, the same shape as production:

| Piece | Local | Port |
|---|---|---|
| API (`src/Server`) | `dotnet run` or Docker | 8080 |
| Blazor frontend (`src/WasmApp`) | `dotnet watch run` dev server | 8443 |

Open **http://localhost:8443**. The Debug build ships `appsettings.Development.json`
with `ApiBaseUrl` pointing at `http://localhost:8080/`, and the API's Development
settings allow that origin. Hard-refresh (Ctrl+Shift+R) after a rebuild, because the
Blazor service worker caches aggressively.

## Login

Two options:

- **Test user (default).** `appsettings.Development.json` for the API sets
  `IsTestUserEnabled: true`, so every request is the test user and the frontend goes
  straight to the timeline. No provider needed.
- **A real provider.** Register a GitHub OAuth app with callback
  `http://localhost:8080/auth/callback/github`, then run the API with
  `RssAppConfig__IsTestUserEnabled=false` and
  `RssAppConfig__Auth__Providers__github__ClientId` / `__ClientSecret` set. The home
  page offers the configured providers. Google and Microsoft work the same way.

The API's session cookie is `SameSite=Lax` in Development (both ports are the same
site, `localhost`), `None; Secure` in production.

## 1. Backend

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/Server/Server.csproj --no-launch-profile --urls http://localhost:8080
```

The database is wherever `RssAppConfig:DbLocation` points in
`appsettings.Development.json`. To run the production image instead:

```powershell
docker build -f src/Server/Dockerfile -t rss-reader-api:local .
docker run -d --name rss-reader-test -p 8080:8080 `
  -v C:\dev\rssreader\docker-data:/data `
  -e RssAppConfig__IsTestUserEnabled=true `
  -e RssAppConfig__Auth__CookieSameSite=Lax `
  -e RssAppConfig__Auth__AllowedFrontendOrigins__0=http://localhost:8443 `
  rss-reader-api:local
```

Add `-e RssAppConfig__BackupDbPath=/data/db/storage.db` to exercise the seed and backup
path against the mounted folder; without it the database is thrown away with the
container.

Confirm with:

```powershell
Invoke-WebRequest http://localhost:8080/api/healthz -UseBasicParsing
Invoke-WebRequest http://localhost:8080/auth/me -UseBasicParsing
```

## 2. Frontend

```powershell
dotnet watch run --project src/WasmApp/WasmApp.csproj --no-launch-profile --urls http://localhost:8443
```

Edits to `.razor` files hot-reload. Start the two processes one after the other, not
in parallel: both build `src/Shared` and race on its output.

### From a git worktree

The `.claude/launch.json` entries in the main checkout start the pieces separately
(`rss-api`, `rss-wasm`) and point at a worktree path; update that path when you switch
worktrees. Stale `blazor-devserver` processes can hold 8443 and keep serving an old
build; kill them before restarting.

## 3. Tests

```powershell
dotnet test test/SerializerTests/SerializerTests.csproj
```

Tests that fetch live third-party feeds are tagged `Network` and excluded in CI; run
them with `--filter TestCategory=Network`. The load scenarios under `test/load/` run
against a local stack; see [test/load/REPORT.md](test/load/REPORT.md).

## Stopping

```powershell
docker rm -f rss-reader-test
Get-Process -Name Server, blazor-devserver -ErrorAction SilentlyContinue | Stop-Process
```
