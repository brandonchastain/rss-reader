# Local testing

The local stack has the same shape as production: the API on **8080** and the Blazor
frontend on **8443**. Open <http://localhost:8443>. You are signed in as a test user, and
no sign-in provider is needed.

You need the .NET 10 SDK and Docker Desktop. Node is needed only for the browser tests,
and k6 only for the load tests.

## Run

Start the API, either in Docker (the production image) or with `dotnet run`:

```powershell
docker build -f src/Server/Dockerfile -t rss-reader-api:local .
docker run -d --name rss-reader-test -p 8080:8080 `
  -e ASPNETCORE_ENVIRONMENT=Development `
  -e RssAppConfig__IsTestUserEnabled=true `
  -e RssAppConfig__Auth__CookieSameSite=Lax `
  -e RssAppConfig__Auth__AllowedFrontendOrigins__0=http://localhost:8443 `
  rss-reader-api:local
```

```powershell
# or: no Docker, uses src/Server/appsettings.Development.json
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/Server/Server.csproj --no-launch-profile --urls http://localhost:8080
```

Then start the frontend. Wait until the API is up first, because both build `src/Shared`
and would race:

```powershell
dotnet run -c Debug --project src/WasmApp/WasmApp.csproj
```

Use a **Debug** build. A Release build leaves out `appsettings.Development.json`, so the
frontend would call the production API.

The Docker database is lost with the container. To keep it, add
`-v C:\dev\rssreader\docker-data:/data -e RssAppConfig__DbLocation=/data/storage.db`.

## Sign in with a real provider

1. Register a GitHub OAuth app with callback `http://localhost:8080/auth/callback/github`.
2. Run the API with `RssAppConfig__IsTestUserEnabled=false`.
3. Set `RssAppConfig__Auth__Providers__github__ClientId` and `__ClientSecret`.

Google and Microsoft work the same way.

## Test

```powershell
dotnet test test/RssReaderTests/RssReaderTests.csproj --filter "TestCategory!=Network"
```

- The filter skips tests that fetch live feeds.
- The writer-lease tests need Azurite and `RSSREADER_TEST_AZURITE_CONNECTION` (see
  `.github/workflows/ci.yml`). Without them, those tests are inconclusive.
- Browser tests: [test/e2e/README.md](test/e2e/README.md).
- Durability test (Docker, real Litestream): `bash test/durability/run.sh`.
- Load tests: [test/load/README.md](test/load/README.md).

## Stop

```powershell
docker rm -f rss-reader-test
Get-Process -Name Server, blazor-devserver -ErrorAction SilentlyContinue | Stop-Process
```

A leftover `blazor-devserver` process can keep port 8443 and serve an old build. Kill it
before restarting.
