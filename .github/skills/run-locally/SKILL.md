---
name: run-locally
description: Start the full RSS Reader stack locally. Use this when asked to run the project locally, start the local environment, or start the dev server. Handles starting Docker Desktop, building the backend image, running the API container, and launching the SWA frontend dev server.
---

Start the full RSS Reader local development environment by following these steps in order.

## Step 1: Ensure Docker Desktop is running

Run `docker info` to check if Docker is running. If the command fails or returns an error:

1. Start Docker Desktop:
   ```powershell
   Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe"
   ```
2. Wait for the Docker daemon to become ready by polling `docker info` every 5 seconds, up to 60 seconds total. Print a waiting message each poll. If Docker is not ready after 60 seconds, stop and report the error.

## Step 2: Build the Docker image

Navigate to the `src\` directory (parent of `Server\` and `Shared\`) and build the image:

```powershell
cd C:\Users\brand\dev\rssreader\rss-reader\src
docker build -f Server/Dockerfile -t rss-reader-api:local .
```

This may take a few minutes on first build. Report progress as output streams.

## Step 3: Create the persistent data directory

```powershell
New-Item -ItemType Directory -Path "C:\dev\rssreader\docker-data" -Force
```

## Step 4: Clean up any existing container

If a container named `rss-reader-test` already exists (running or stopped), remove it:

```powershell
docker rm -f rss-reader-test
```

It is safe to ignore errors here if no container exists.

## Step 5: Run the backend container

```powershell
docker run -d `
  --name rss-reader-test `
  -p 8080:8080 `
  -v C:\dev\rssreader\docker-data:/data `
  -e RssAppConfig__IsTestUserEnabled=true `
  rss-reader-api:local
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

## Step 8: Start the SWA frontend dev server

From the repository root (`C:\Users\brand\dev\rssreader\rss-reader`), start the SWA dev server in the background:

```powershell
cd C:\Users\brand\dev\rssreader\rss-reader
swa start rss-reader-local
```

The SWA CLI reads `swa-cli.config.json` and:
- Serves the Blazor WASM frontend
- Proxies `/api/*` calls to the Azure Functions proxy (which in turn forwards to the backend)
- Simulates Easy Auth locally

The frontend will be available at **http://localhost:4280**

The SWA CLI proxies to the Blazor dev server at `http://localhost:8443` (set in `swa-cli.config.json`).

## Final summary

Report to the user:
- ✅ Backend API: http://localhost:8080
- ✅ Frontend: http://localhost:4280
- Test user is enabled (`RssAppConfig__IsTestUserEnabled=true`) — authentication is bypassed
- To view backend logs: `docker logs -f rss-reader-test`
- To stop: `docker rm -f rss-reader-test`

## Troubleshooting

- **Port 8080 already in use**: Run `netstat -ano | Select-String ":8080"` to find the process, then either kill it or refer to LOCAL-TESTING.md for alternate port instructions.
- **Container stops immediately**: Run `docker logs rss-reader-test` to see startup errors.
- **Database errors**: Check `docker exec rss-reader-test ls -la /tmp/` to verify the db file was created.
