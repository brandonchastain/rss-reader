# Copilot Instructions

## Architecture

Three-tier application hosted on Azure:

```
Browser → Azure Static Web Apps (Easy Auth)
              → /api/* → Azure Functions proxy (api/)
                            → ASP.NET Core backend (src/Server/)
                                  → SQLite database
```

- **`src/WasmApp/`** — Blazor WebAssembly frontend (C#, Razor, Bootstrap)
- **`src/Server/`** — ASP.NET Core Web API backend with SQLite
- **`src/Shared/`** — Shared DTOs/contracts used by both frontend and backend
- **`api/`** — Azure Functions v4 (Node.js) API proxy
- **`test/SerializerTests/`** — MSTest unit tests (net9.0)
- **`infrastructure/`** — Bicep templates for Azure Container Apps + SWA

## Build & Test

```powershell
# Build the whole solution
dotnet build rss-reader.sln

# Run all tests
dotnet test rss-reader.sln

# Run a single test class
dotnet test test/SerializerTests --filter "ClassName=SerializerTests.SerializerTests"

# Build Docker image for the backend (run from src/)
docker build -f Server/Dockerfile -t rss-reader-api:local .

# Build and deploy the frontend (run from rss-reader/)
swa build
swa deploy --env production
```

**Local dev**: Hit F5 in Visual Studio — launches frontend and backend together.

For containerized backend testing, see [LOCAL-TESTING.md](../LOCAL-TESTING.md).

## Authentication Flow

Authentication is a two-stage chain:

1. **Azure SWA Easy Auth** authenticates the browser via AAD (login at `/.auth/login/aad`). SWA injects `x-ms-client-principal` (base64 JSON) into requests to the Function proxy — browsers cannot forge this.

2. **Azure Functions proxy** (`api/src/functions/ApiProxy.js`) extracts the Easy Auth principal, then forwards requests to the backend with:
   - `X-Gateway-Key`: a shared secret (env var `RSSREADER_API_KEY`)
   - `X-User-Id`: the raw base64 `x-ms-client-principal` value

3. **Backend** (`StaticWebAppsAuthenticationHandler`) validates `X-Gateway-Key` against `RSSREADER_API_KEY` env var, then parses `X-User-Id` to build the `ClaimsPrincipal`.

For local development, set `RssAppConfig__IsTestUserEnabled=true` to bypass auth with a fake test user (`testuser2`).

## Key Conventions

### Shared Contracts
All DTOs live in `src/Shared/Contracts/`: `NewsFeed`, `NewsFeedItem`, `RssUser`, `OpmlImport`. Both the frontend (`FeedClient`/`UserClient`) and backend controllers use these same types.

### Repository Pattern
The backend uses interfaces (`IFeedRepository`, `IItemRepository`, `IUserRepository`) with SQLite implementations in `src/Server/Data/`. Repositories are registered as singletons and initialize their own database tables on construction. The order of singleton creation matters (see `Program.cs` — feed → user → item).

### Background Work
Feed refresh runs via a `BackgroundWorkQueue` + `BackgroundWorker` hosted service. Enqueue work items to `BackgroundWorkQueue`; `FeedRefresher` handles the actual HTTP fetch and parse. Don't call `FeedRefresher` directly from controllers — enqueue instead.

### Configuration
App config is loaded into `RssAppConfig` from `appsettings.json` under the `RssAppConfig` section. The backend reads `DbLocation` for the SQLite path (`/tmp/storage.db` in Docker, copied to `/data/` for persistence). Frontend config is in `RssWasmConfig`.

### DatabaseBackupService
The SQLite database lives at `/tmp/storage.db` (ephemeral container storage) and is periodically backed up to `/data/storage.db` (Azure Files volume mount) every 5 minutes. On container startup, `Program.cs` explicitly calls `RestoreFromBackupAsync` before any repository is instantiated — this ordering is intentional.

The backup cycle uses **SQLite's native backup API** (`SqliteConnection.BackupDatabase`) to create a consistent point-in-time snapshot at `/tmp/storage-backup.db`, then computes a SHA256 hash to skip the Azure Files write if nothing changed (reducing transaction costs). Images in `wwwroot/images/` are also synced to `/data/images/` on the same cycle, but only new files are copied (no overwrites).

On shutdown, `StopAsync` attempts a best-effort final backup with a 5-second timeout. If it fails or times out, the last periodic backup (at most 5 minutes old) provides coverage.

### OPML Import/Export
OPML import/export is handled by the static `OpmlSerializer` class (`src/Server/Services/Serialization/OpmlSerializer.cs`), shared between the backend and tests.

- **Export** (`GET /api/feed/exportOpml?userId=...`): Fetches the user's feeds and serializes to OPML 2.0 XML. Feed tags are written as a comma-separated `category` attribute on each `<outline>` element.
- **Import** (`POST /api/feed/importOpml`): Parses OPML XML, reads `category` attribute for tags, and bulk-inserts feeds via `feedRepository.AddFeeds`. Both endpoints enforce that the requesting user can only import/export their own data (comparing `ClaimTypes.Name` against the requested `userId`).

Security: The XML parser has DTD processing disabled and a 1MB content size limit to prevent XXE and DoS attacks. Do not change these settings.

### HTTP Redirects
`RedirectDowngradeHandler` is registered on the `RssClient` `HttpClient` to prevent HTTPS→HTTP redirect downgrade attacks when fetching external RSS feeds.

### Frontend HTTP Clients
The Blazor app uses named `HttpClient` instances via `IHttpClientFactory`. The client named `"ApiClient"` is used by `FeedClient` and `UserClient` to call the backend through the SWA proxy.
