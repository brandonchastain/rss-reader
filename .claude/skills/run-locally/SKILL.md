---
name: run-locally
description: Start or stop the rss-reader stack on this machine (API in Docker on 8080, Blazor frontend on 8443, signed in as a test user). Use when asked to run the app locally, start the dev server, or stop/clean up local servers.
---

Run every command from the repository root (the worktree, if you are in one). Don't
elevate with `-Verb RunAs`: Docker Desktop's pipe belongs to the current user.

## Start
1. **Docker.** If `docker info` fails, start Docker Desktop through the Windows shell,
   with the sandbox disabled for this one command. Never use `Start-Process`: Docker
   Desktop inherits the agent's process and fails on its socket files.
   ```powershell
   explorer.exe "C:\Program Files\Docker\Docker\Docker Desktop.exe"
   ```
   Poll `docker info` every 5 s for up to 60 s.
2. **API.** Remove any old container, then build and run:
   ```powershell
   docker rm -f rss-reader-test 2>$null
   docker build -f src/Server/Dockerfile -t rss-reader-api:local .
   docker run -d --name rss-reader-test -p 8080:8080 `
     -e ASPNETCORE_ENVIRONMENT=Development `
     -e RssAppConfig__IsTestUserEnabled=true `
     -e RssAppConfig__Auth__CookieSameSite=Lax `
     -e RssAppConfig__Auth__AllowedFrontendOrigins__0=http://localhost:8443 `
     rss-reader-api:local
   ```
   Wait for `http://localhost:8080/api/healthz` to return 200. If it doesn't, read
   `docker logs rss-reader-test`.
3. **Frontend**, in the background. It must be a Debug build, because a Release build
   calls the production API.
   ```powershell
   dotnet run -c Debug --project src/WasmApp/WasmApp.csproj
   ```
   Open http://localhost:8443.

## Stop
```powershell
docker rm -f rss-reader-test
Get-Process -Name Server, blazor-devserver -ErrorAction SilentlyContinue | Stop-Process
```
A leftover `blazor-devserver` can keep port 8443 and serve an old build.

More detail is in LOCAL-TESTING.md.
