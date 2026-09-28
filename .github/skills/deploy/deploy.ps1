# deploy.ps1
# Deploys the RSS Reader app to production by running the Deploy GitHub Actions
# workflow (.github/workflows/deploy.yml) and waiting for it. The workflow builds
# and pushes the backend image, deploys the Bicep template, and publishes the SWA
# frontend. Nothing is built or pushed from this machine, and no token is needed
# beyond an authenticated `gh` CLI.
#
# Usage: .\deploy.ps1            # dispatch a new run of main and wait for it
#        .\deploy.ps1 -Watch     # only wait for the newest run already in flight
# Requires: gh CLI (gh auth login), az CLI for the post-deploy revision check.

[CmdletBinding()]
param(
    [switch]$Watch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Workflow = "deploy.yml"
$Branch   = "main"

# ── Step 1: Prerequisites ────────────────────────────────────────────────────

gh auth status *>$null 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "gh is not authenticated. Run 'gh auth login' and retry."
    exit 1
}

# ── Step 2: Dispatch (unless only watching) ──────────────────────────────────

if (-not $Watch) {
    Write-Host "Dispatching $Workflow on $Branch..." -ForegroundColor Cyan
    gh workflow run $Workflow --ref $Branch
    if ($LASTEXITCODE -ne 0) { Write-Error "gh workflow run failed."; exit 1 }
    # The run takes a moment to appear in the list.
    Start-Sleep -Seconds 5
}

# ── Step 3: Wait for the newest run ──────────────────────────────────────────

$runJson = gh run list --workflow $Workflow --branch $Branch --limit 1 --json databaseId,headSha,status,url --jq '.[0]'
if ($LASTEXITCODE -ne 0 -or -not $runJson) { Write-Error "Could not find a $Workflow run on $Branch."; exit 1 }
$run = $runJson | ConvertFrom-Json

Write-Host "Watching run $($run.databaseId) for commit $($run.headSha.Substring(0,7))" -ForegroundColor Cyan
Write-Host "  $($run.url)"
gh run watch $run.databaseId --exit-status
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nDeploy run failed. Failing job output:" -ForegroundColor Red
    gh run view $run.databaseId --log-failed
    exit 1
}

# ── Step 4: Post-deploy check ────────────────────────────────────────────────

Write-Host "`nChecking backend health..." -ForegroundColor Cyan
$health = Invoke-WebRequest -UseBasicParsing -TimeoutSec 120 https://rss.brandonchastain.com/api/healthz
Write-Host "  /api/healthz -> HTTP $([int]$health.StatusCode): $($health.Content)"

az version *>$null 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "`nActive revision:" -ForegroundColor Cyan
    az containerapp revision list --name rss-reader-api --resource-group rss-container-rg `
        --query "[?properties.active].{name:name,traffic:properties.trafficWeight,state:properties.runningState,health:properties.healthState}" -o table
}

Write-Host "`n✅ Deploy run $($run.databaseId) succeeded for $($run.headSha.Substring(0,7))" -ForegroundColor Green
Write-Host "✅ https://rss.brandonchastain.com is serving the new build" -ForegroundColor Green
