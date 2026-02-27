# start-local.ps1
# Starts the RSS Reader full local stack: Docker backend + SWA frontend
# Usage: .\start-local.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = "C:\Users\brand\dev\rssreader\rss-reader"
$SrcDir = Join-Path $RepoRoot "src"
$DataDir = "C:\dev\rssreader\docker-data"
$ImageName = "rss-reader-api:local"
$ContainerName = "rss-reader-test"

# ── Step 1: Ensure Docker is running ─────────────────────────────────────────
Write-Host "Checking Docker..." -ForegroundColor Cyan
$dockerReady = $false
docker info *>$null 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker not running. Starting Docker Desktop..." -ForegroundColor Yellow
    Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe"
    $timeout = 60
    $elapsed = 0
    while ($elapsed -lt $timeout) {
        Start-Sleep -Seconds 5
        $elapsed += 5
        docker info *>$null 2>&1
        if ($LASTEXITCODE -eq 0) {
            $dockerReady = $true
            Write-Host "Docker is ready." -ForegroundColor Green
            break
        }
        Write-Host "  Waiting for Docker... ($elapsed/$timeout s)"
    }
    if (-not $dockerReady) {
        Write-Error "Docker did not start within $timeout seconds. Check Docker Desktop."
        exit 1
    }
} else {
    Write-Host "Docker is already running." -ForegroundColor Green
}

# ── Step 2: Build the image ───────────────────────────────────────────────────
Write-Host "`nBuilding Docker image '$ImageName'..." -ForegroundColor Cyan
Push-Location $SrcDir
try {
    docker build -f Server/Dockerfile -t $ImageName .
    if ($LASTEXITCODE -ne 0) { Write-Error "Docker build failed."; exit 1 }
} finally {
    Pop-Location
}
Write-Host "Image built successfully." -ForegroundColor Green

# ── Step 3: Create data directory ─────────────────────────────────────────────
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

# ── Step 4: Remove existing container ─────────────────────────────────────────
$existing = docker ps -a --filter "name=$ContainerName" --format "{{.Names}}" 2>$null
if ($existing -eq $ContainerName) {
    Write-Host "`nRemoving existing container '$ContainerName'..." -ForegroundColor Yellow
    docker rm -f $ContainerName | Out-Null
}

# ── Step 5: Run the container ─────────────────────────────────────────────────
Write-Host "`nStarting backend container..." -ForegroundColor Cyan
docker run -d `
    --name $ContainerName `
    -p 8080:8080 `
    -v "${DataDir}:/data" `
    -e RssAppConfig__IsTestUserEnabled=true `
    $ImageName

if ($LASTEXITCODE -ne 0) { Write-Error "Failed to start container."; exit 1 }

# ── Step 6: Health check ──────────────────────────────────────────────────────
Write-Host "`nWaiting for backend to be healthy..." -ForegroundColor Cyan
$healthUrl = "http://localhost:8080/api/healthz"
$healthy = $false
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Seconds 3
    try {
        $resp = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 3
        if ($resp.StatusCode -eq 200) {
            $healthy = $true
            break
        }
    } catch { }
    Write-Host "  Still waiting... ($([int](($i+1)*3))/30 s)"
}

if (-not $healthy) {
    Write-Host "`nBackend health check failed. Container logs:" -ForegroundColor Red
    docker logs $ContainerName
    exit 1
}
Write-Host "Backend API is healthy." -ForegroundColor Green

# ── Step 7: Ensure api/local.settings.json exists ────────────────────────────
$localSettings = Join-Path $RepoRoot "api\local.settings.json"
if (-not (Test-Path $localSettings)) {
    Write-Host "`nCreating api/local.settings.json..." -ForegroundColor Cyan
    @'
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "node"
  }
}
'@ | Set-Content $localSettings
}

# ── Step 8: Start SWA dev server ─────────────────────────────────────────────
Write-Host "`nStarting SWA frontend dev server..." -ForegroundColor Cyan
Write-Host "Frontend will be available at http://localhost:4280" -ForegroundColor Green
Write-Host "(Press Ctrl+C to stop the SWA dev server)`n"

Set-Location $RepoRoot
swa start rss-reader-local
