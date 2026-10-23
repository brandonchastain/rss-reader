---
name: stop-local
description: Stop all locally running RSS Reader servers. Use this when asked to stop the local environment, shut down the dev servers, or clean up local processes. Kills the Blazor dev server (port 8443), dotnet watch processes, and the backend Docker container.
---

Stop all local RSS Reader servers by following these steps in order.

## Step 1: Stop the backend Docker container

Run Docker commands directly in the current user context — **do not use `-Verb RunAs` or any UAC elevation**. Docker Desktop's named pipe (`dockerDesktopLinuxEngine`) is only accessible to the current user; elevated shells lose access to it and will get "cannot find the file specified" errors.

```powershell
docker rm -f rss-reader-test 2>&1
```

It is safe to ignore errors if the container is not running.

## Step 2: Kill the process on port 8443 (Blazor dev server / dotnet watch)

```powershell
$pids = netstat -ano | Select-String ":8443" | ForEach-Object { ($_ -split '\s+')[-1] } | Where-Object { $_ -match '^\d+$' -and $_ -ne '0' } | Select-Object -Unique
foreach ($p in $pids) { Stop-Process -Id $p -Force -ErrorAction SilentlyContinue }
```

## Step 3: Verify all ports are cleared

```powershell
netstat -ano | Select-String ":8443"
```

If any ports are still listed as LISTENING, repeat the kill step for those ports.

## Final summary

Report to the user:
- ✅ Backend container stopped
- ✅ Blazor dev server (8443) — stopped
