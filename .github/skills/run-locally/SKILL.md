---
name: run-locally
description: Start the full RSS Reader stack locally. Use this when asked to run the project locally, start the local environment, or start the dev server. Handles starting Docker Desktop, building the backend image, running the API container, and launching the SWA frontend dev server.
---

Start the full RSS Reader local development environment by following these steps in order.

## Step 1: Stop any existing local servers

Before starting, invoke the **stop-local** skill to clean up any previously running instances. This prevents port conflicts and stale processes.

## Step 2: Ensure Docker Desktop is running

Run `docker info` to check if Docker is running. If the command fails or returns an error:

1. Start Docker Desktop:
   ```powershell
   Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe"
   ```
2. Wait for the Docker daemon to become ready by polling `docker info` every 5 seconds, up to 60 seconds total. Print a waiting message each poll. If Docker is not ready after 60 seconds, stop and report the error.

## Step 3: Build the Docker image

Docker commands require UAC elevation on this machine. Use `Start-Process` with `-Verb RunAs` and a temp log file to capture output:

```powershell
$log = "$env:TEMP\docker-build.log"
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -Command `"cd 'C:\Users\brand\dev\rssreader\rss-reader\src'; docker build -f Server/Dockerfile -t rss-reader-api:local . 2>&1 | Tee-Object '$log'`"" -Wait
Get-Content $log
```

This may take a few minutes on first build. Report progress from the log after it completes.

## Step 4: Create the persistent data directory

```powershell
New-Item -ItemType Directory -Path "C:\dev\rssreader\docker-data" -Force
```

## Step 5: Run the backend container

```powershell
$log = "$env:TEMP\docker-run.log"
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -Command `"docker run -d --name rss-reader-test -p 8080:8080 -v C:\dev\rssreader\docker-data:/data -e RssAppConfig__IsTestUserEnabled=true rss-reader-api:local 2>&1 | Tee-Object '$log'`"" -Wait
Get-Content $log
```

## Step 6: Verify the backend is healthy

Poll `http://localhost:8080/api/healthz` every 3 seconds for up to 30 seconds until it returns HTTP 200. Use:

```powershell
Invoke-WebRequest -Uri "http://localhost:8080/api/healthz" -UseBasicParsing
```

If healthy, report: "✅ Backend API is running at http://localhost:8080"
If not healthy after 30 seconds, run `docker logs rss-reader-test` and report the failure with logs.

## Step 7: Ensure `api/local.settings.json` exists

The Azure Functions host requires this file. Create it if missing:

```powershell
$path = "C:\Users\brand\dev\rssreader\rss-reader\api\local.settings.json"
if (-not (Test-Path $path)) {
    @'
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
'@ | Set-Content $path
}
```

This file is gitignored — it must be created locally on each new checkout.

## Step 8: Build the Blazor WASM frontend (**DEBUG build — MANDATORY**)

> ⛔ **NEVER use `dotnet publish -c release` for local development.** A release publish excludes `appsettings.Development.json` (`CopyToPublishDirectory = Never`), which is the file that enables `EnableTestAuth: true`. Without it, SWA Easy Auth is enforced and all API calls return 401. **Always use the Debug build.**

Run a Debug build, then create the output directory placeholder required by `swa-cli.config.json`:

```powershell
cd C:\Users\brand\dev\rssreader\rss-reader\src\WasmApp
dotnet build -c Debug WasmApp.csproj
New-Item -ItemType Directory -Path "bin/release/net9.0/publish/wwwroot" -Force | Out-Null
```

This may take a minute or two. If the build fails, report the error and stop.

> **How the debug dev server works:** `swa start rss-reader-local` uses `dotnet watch run` (a Kestrel dev server on port 8443) as the app dev server. SWA proxies all frontend requests to it, serving files directly from source `wwwroot/` — including `appsettings.Development.json`. The `bin/release/net9.0/publish/wwwroot` directory is created as an empty placeholder so SWA CLI's config validation doesn't fail — it is **not** the served output.

## Step 9: Start the SWA frontend dev server

From the repository root (`C:\Users\brand\dev\rssreader\rss-reader`), start the SWA dev server **detached** so it persists after the agent session ends:

```powershell
cd C:\Users\brand\dev\rssreader\rss-reader
swa start rss-reader-local
```

**Important:** Use `mode="async"` with `detach: true` when calling this via the powershell tool, so the process is fully detached and survives session shutdown.

The SWA CLI reads `swa-cli.config.json` and:
- Serves the Blazor WASM frontend
- Proxies `/api/*` calls to the Azure Functions proxy (which in turn forwards to the backend)
- Simulates Easy Auth locally

The frontend will be available at **http://localhost:4280**

To verify port 4280 is listening after launch, check with:
```powershell
netstat -ano | Select-String ":4280"
```
Note: it binds to `127.0.0.1:4280`, not `0.0.0.0:4280`, so filter for `:4280` not `0.0.0.0:4280`.

The SWA CLI proxies to the Blazor dev server at `http://localhost:8443` (set in `swa-cli.config.json`).

## Final summary



Report to the user:
- ✅ Backend API: http://localhost:8080
- ✅ Frontend: http://localhost:4280
- Test user is enabled (`RssAppConfig__IsTestUserEnabled=true`) — authentication is bypassed
- To view backend logs: `docker logs -f rss-reader-test`
- To stop: invoke the **stop-local** skill
- **⚠️ Hard refresh required**: Blazor WASM uses a service worker that aggressively caches the app. Open http://localhost:4280 and press **Ctrl+Shift+R** (or Cmd+Shift+R on Mac) to bypass the cache, or open in a private/incognito window.

## Troubleshooting

- **Port 8080 already in use**: Run `netstat -ano | Select-String ":8080"` to find the process, then either kill it or refer to LOCAL-TESTING.md for alternate port instructions.
- **Container stops immediately**: Run `docker logs rss-reader-test` to see startup errors.
- **Database errors**: Check `docker exec rss-reader-test ls -la /tmp/` to verify the db file was created.
