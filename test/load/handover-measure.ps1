# Deploy-handover measurement: a large database in a container of the production shape (0.5 vCPU, 1 GiB).
# Usage: test/load/handover-measure.ps1 -Share C:\path\to\share   (expects <Share>\db\storage.db; see REPORT.md)
# Build the image first: docker build -f src/Server/Dockerfile -t rss-reader-api:delta .
param([string]$Share, [string]$Image = "rss-reader-api:delta", [string]$Name = "delta-test")
$ErrorActionPreference = "Continue"
docker rm -f $Name 2>$null | Out-Null
Remove-Item "$Share\db\storage.db.delta","$Share\db\storage.db.lock" -ErrorAction SilentlyContinue
$t0 = Get-Date
docker run -d --name $Name --cpus 0.5 --memory 1g -p 8081:8080 -v "${Share}:/data" `
  -e ASPNETCORE_ENVIRONMENT=Development -e RssAppConfig__IsTestUserEnabled=true `
  -e RssAppConfig__SchedulerEnabled=false -e RssAppConfig__BackupDbPath=/data/db/storage.db `
  -e RssAppConfig__BackupInterval=00:00:20 -e Logging__LogLevel__Default=Information `
  -e Logging__LogLevel__Microsoft=Warning $Image | Out-Null
Write-Host "started; waiting for seed + first full cycle"
$deadline = (Get-Date).AddMinutes(15)
do { Start-Sleep 5; $logs = docker logs $Name 2>&1 | Out-String } until ($logs -match "DB backup OK" -or (Get-Date) -gt $deadline)
$logs -split "`n" | Where-Object { $_ -match "Seeded|Applied|DB backup OK|delta|Shutdown|error|fail" } | ForEach-Object { Write-Host "  $_" }
# A write after the periodic copy: register the test user and add a feed.
try { Invoke-WebRequest -Uri http://localhost:8081/api/user/register -Method POST -TimeoutSec 20 -SkipHttpErrorCheck | Select-Object -Expand StatusCode | ForEach-Object { Write-Host "register: $_" } } catch { Write-Host "register: $_" }
try { Invoke-WebRequest -Uri http://localhost:8081/api/feed -Method POST -Body '{"Href":"https://xkcd.com/rss.xml","UserId":1}' -ContentType application/json -TimeoutSec 60 -SkipHttpErrorCheck | Select-Object -Expand StatusCode | ForEach-Object { Write-Host "add feed: $_" } } catch { Write-Host "add feed: $_" }
Start-Sleep 2
Write-Host "stopping (SIGTERM, 120 s grace)"
$t1 = Get-Date
docker stop -t 120 $Name | Out-Null
$stopSeconds = ((Get-Date) - $t1).TotalSeconds
Write-Host ("docker stop took {0:N1} s" -f $stopSeconds)
docker logs --since "$($t1.ToUniversalTime().ToString('o'))" $Name 2>&1 | Where-Object { $_ -match "delta|Shutdown|backup|error|fail" } | ForEach-Object { Write-Host "  $_" }
Get-ChildItem "$Share\db" | ForEach-Object { Write-Host ("  {0} {1:N0} bytes" -f $_.Name, $_.Length) }
Write-Host "restarting to seed from base + delta"
$t2 = Get-Date
docker start $Name | Out-Null
do { Start-Sleep 3; $logs = docker logs --since "$($t2.ToUniversalTime().ToString('o'))" $Name 2>&1 | Out-String } until ($logs -match "Seeded|quick_check failed|Refusing" -or (Get-Date) -gt (Get-Date).AddMinutes(10))
$logs -split "`n" | Where-Object { $_ -match "Seeded|Applied|Ignoring|delta|error|fail" } | ForEach-Object { Write-Host "  $_" }
do { Start-Sleep 3; $h = try { (Invoke-WebRequest -Uri http://localhost:8081/api/healthz -TimeoutSec 5).Content } catch { "" } } until ($h -match "healthy" -and $h -notmatch "seeding" -or (Get-Date) -gt (Get-Date).AddMinutes(5))
Write-Host "healthz: $h"
try { $f = (Invoke-WebRequest -Uri http://localhost:8081/api/feed -TimeoutSec 20).Content; Write-Host "feeds after restart: $(($f | ConvertFrom-Json).Count)" } catch { Write-Host "feed list: $_" }
docker stop -t 120 $Name | Out-Null
docker logs --since "$($t2.ToUniversalTime().ToString('o'))" $Name 2>&1 | Where-Object { $_ -match "delta|Shutdown" } | ForEach-Object { Write-Host "  $_" }
