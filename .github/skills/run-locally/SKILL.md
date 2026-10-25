---
name: run-locally
description: Start the full RSS Reader stack locally. Use this when asked to run the project locally, start the local environment, or start the dev server. Handles starting Docker Desktop, building the backend image, running the API container, and launching the Blazor frontend dev server.
---

Start the full RSS Reader local development environment by following these steps in order.

## Step 1: Stop any existing local servers

Before starting, invoke the **stop-local** skill to clean up any previously running instances. This prevents port conflicts and stale processes.

## Step 2: Ensure Docker Desktop is running

Run `docker info` to check if Docker is running. If the command fails or returns an error:

1. Start Docker Desktop **through the Windows shell, not as a child of the agent's
   shell**. A `Start-Process` launch from an agent tool makes Docker Desktop inherit
   that process, and it then fails during startup with "The file cannot be accessed
   by the system" on its Unix socket files (`dockerInference`, `engine.sock`),
   showing an error dialog repeatedly. Launching via `explorer.exe` detaches it
   exactly like clicking it in the Start menu. If the tool runs sandboxed, disable
   the sandbox for this one command.
   ```powershell
   explorer.exe "C:\Program Files\Docker\Docker\Docker Desktop.exe"
   ```
   If a previous failed launch left Docker Desktop open on that error dialog,
   stop it first: `Get-Process 'Docker Desktop','com.docker.backend' -ErrorAction SilentlyContinue | Stop-Process -Force`.
2. Wait for the Docker daemon to become ready by polling `docker info` every 5 seconds, up to 60 seconds total. Print a waiting message each poll. If Docker is not ready after 60 seconds, stop and report the error.

## Step 3: Build the Docker image

Run Docker commands directly in the current user context — **do not use `-Verb RunAs` or any UAC elevation**. Docker Desktop's named pipe (`dockerDesktopLinuxEngine`) is only accessible to the current user; elevated shells lose access to it and produce "cannot find the file specified" errors.

```powershell
cd C:\Users\brand\dev\rssreader\rss-reader
docker build -f src/Server/Dockerfile -t rss-reader-api:local .
```

This may take a few minutes on first build.

## Step 4: Run the backend container

```powershell
docker run -d --name rss-reader-test -p 8080:8080 `
  -e ASPNETCORE_ENVIRONMENT=Development `
  -e RssAppConfig__Auth__AllowedFrontendOrigins__0=http://localhost:8443 `
  -e RssAppConfig__IsTestUserEnabled=true `
  rss-reader-api:local
```

## Step 5: Verify the backend is healthy

Poll `http://localhost:8080/api/healthz` every 3 seconds for up to 30 seconds until it returns HTTP 200. Use:

```powershell
Invoke-WebRequest -Uri "http://localhost:8080/api/healthz" -UseBasicParsing
```

If healthy, report: "✅ Backend API is running at http://localhost:8080"
If not healthy after 30 seconds, run `docker logs rss-reader-test` and report the failure with logs.

## Step 6: Build the Blazor WASM frontend (**DEBUG build — MANDATORY**)

> ⛔ **NEVER use `dotnet publish -c release` for local development.** A release publish excludes `appsettings.Development.json` (`CopyToPublishDirectory = Never`), which is the file that enables `EnableTestAuth: true`. Without it, SWA Easy Auth is enforced and all API calls return 401. **Always use the Debug build.**

Run a Debug build:

```powershell
cd C:\Users\brand\dev\rssreader\rss-reader\src\WasmApp
dotnet build -c Debug WasmApp.csproj
```

This may take a minute or two. If the build fails, report the error and stop.

> **Note:** the API refuses to start with the test user enabled unless `ASPNETCORE_ENVIRONMENT` is `Development` (a fake administrator in Production would be a full bypass), which is why Step 4 sets it. The image does not carry `appsettings.Development.json`, so the allowed frontend origin (`http://localhost:8443`, for CORS) is passed as an environment variable; test-user mode ignores cookies.

## Step 7: Start the Blazor dev server

The Static Web Apps emulator and the Azure Functions proxy are retired; the frontend talks to the API directly (`ApiBaseUrl` in `appsettings.Development.json` is `http://localhost:8080/`). From the repository root start the dev server **detached**:

```powershell
cd src\WasmApp
dotnet run -c Debug --no-build
```

Use `mode="async"` with `detach: true` when calling this via the powershell tool. The frontend is then at **http://localhost:8443**. Verify with `netstat -ano | Select-String ":8443"`.

## Final summary



Report to the user:
- ✅ Backend API: http://localhost:8080
- ✅ Frontend: http://localhost:8443
- Test user is enabled (`RssAppConfig__IsTestUserEnabled=true`) — authentication is bypassed
- To view backend logs: `docker logs -f rss-reader-test`
- To stop: invoke the **stop-local** skill
- **⚠️ Hard refresh required**: Blazor WASM uses a service worker that aggressively caches the app. Open http://localhost:8443 and press **Ctrl+Shift+R** (or Cmd+Shift+R on Mac) to bypass the cache, or open in a private/incognito window.

## Troubleshooting

- **Port 8080 already in use**: Run `netstat -ano | Select-String ":8080"` to find the process, then either kill it or refer to LOCAL-TESTING.md for alternate port instructions.
- **Container stops immediately**: Run `docker logs rss-reader-test` to see startup errors.
- **Database errors**: Check `docker exec rss-reader-test ls -la /tmp/` to verify the db file was created.
