# deploy.ps1
# Deploys the RSS Reader app to production (no infrastructure/Bicep changes).
# Usage: .\deploy.ps1
# Requires: $env:GITHUB_USERNAME set, Docker running, az CLI, swa CLI

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = "C:\Users\brand\dev\rssreader\rss-reader"
$SrcDir   = Join-Path $RepoRoot "src"

# ── Step 1: Validate prerequisites ───────────────────────────────────────────

Write-Host "Initializing fnm and switching to Node 20..." -ForegroundColor Cyan
fnm env --use-on-cd --shell powershell | Out-String | Invoke-Expression
fnm use 20
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to switch to Node 20 via fnm."; exit 1 }
Write-Host "Node version: $(node --version)" -ForegroundColor Green


if (-not $env:GITHUB_USERNAME) {
    $env:GITHUB_USERNAME = git config github.user
}
if (-not $env:GITHUB_USERNAME) {
    $remoteUrl = git remote get-url origin 2>$null
    if ($remoteUrl -match 'github\.com[:/]([^/]+)/') {
        $env:GITHUB_USERNAME = $Matches[1]
    }
}
if (-not $env:GITHUB_USERNAME) {
    Write-Error "Could not determine GitHub username. Set it with: git config --global github.user 'your-github-username'"
    exit 1
}
Write-Host "Using GitHub username: $env:GITHUB_USERNAME" -ForegroundColor Cyan
$Image = "ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest"

Write-Host "Checking Docker..." -ForegroundColor Cyan
docker info *>$null 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker not running. Starting Docker Desktop..." -ForegroundColor Yellow
    Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe"
    $timeout = 60
    $elapsed = 0
    $dockerReady = $false
    while ($elapsed -lt $timeout) {
        Start-Sleep -Seconds 5
        $elapsed += 5
        docker info *>$null 2>&1
        if ($LASTEXITCODE -eq 0) { $dockerReady = $true; break }
        Write-Host "  Waiting for Docker... ($elapsed/$timeout s)"
    }
    if (-not $dockerReady) {
        Write-Error "Docker did not start within $timeout seconds."
        exit 1
    }
}
Write-Host "Docker is ready." -ForegroundColor Green

az version *>$null 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Azure CLI (az) not found. Install from https://aka.ms/installazurecliwindows"
    exit 1
}

swa --version *>$null 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "SWA CLI not found. Install with: npm install -g @azure/static-web-apps-cli"
    exit 1
}

# ── Step 2: Build & push backend Docker image ─────────────────────────────────

Write-Host "`nBuilding backend Docker image: $Image" -ForegroundColor Cyan
Push-Location $SrcDir
try {
    docker build -t $Image -f Server/Dockerfile .
    if ($LASTEXITCODE -ne 0) { Write-Error "docker build failed."; exit 1 }
} finally {
    Pop-Location
}
Write-Host "Image built successfully." -ForegroundColor Green

Write-Host "`nPushing image to GHCR..." -ForegroundColor Cyan
docker push $Image
if ($LASTEXITCODE -ne 0) {
    Write-Host "docker push failed. You may need to log in to GHCR:" -ForegroundColor Red
    Write-Host "  echo `$env:GITHUB_PAT | docker login ghcr.io -u `$env:GITHUB_USERNAME --password-stdin"
    exit 1
}
Write-Host "Image pushed successfully." -ForegroundColor Green

# ── Step 3: Update Azure Container App ───────────────────────────────────────

Write-Host "`nUpdating Azure Container App..." -ForegroundColor Cyan
az containerapp update `
    --name rss-reader-api `
    --resource-group rss-container-rg `
    --image $Image
if ($LASTEXITCODE -ne 0) {
    Write-Host "az containerapp update failed. Ensure you are logged in (az login) and the app exists." -ForegroundColor Red
    exit 1
}
Write-Host "Container App updated." -ForegroundColor Green

# ── Step 4: Build & deploy frontend ──────────────────────────────────────────

Write-Host "`nBuilding SWA frontend..." -ForegroundColor Cyan
Set-Location $RepoRoot
swa build
if ($LASTEXITCODE -ne 0) { Write-Error "swa build failed."; exit 1 }

Write-Host "`nDeploying SWA frontend to production..." -ForegroundColor Cyan
swa deploy --env production
if ($LASTEXITCODE -ne 0) {
    Write-Host "swa deploy failed. You may need to run 'swa login' first." -ForegroundColor Red
    exit 1
}

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Host "`n✅ Backend image built and pushed: $Image" -ForegroundColor Green
Write-Host "✅ Azure Container App updated: rss-reader-api" -ForegroundColor Green
Write-Host "✅ Frontend deployed to SWA production environment" -ForegroundColor Green
