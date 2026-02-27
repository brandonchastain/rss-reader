---
name: deploy
description: Deploy the RSS Reader app to production. Use this when asked to deploy, push to production, or release the app. Builds and pushes the backend Docker image, updates the Azure Container App, then builds and deploys the SWA frontend. Does NOT run Bicep templates or change infrastructure.
---

Deploy the RSS Reader app to production by following these steps in order.

## Step 1: Check prerequisites

### Node version
Initialize fnm and switch to Node 20 before running any other commands:

```powershell
fnm env --use-on-cd --shell powershell | Out-String | Invoke-Expression
fnm use 20
```

### GITHUB_USERNAME
Resolve the GitHub username from git config:

```powershell
$env:GITHUB_USERNAME = git config github.user
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
Write-Host "Using GitHub username: $env:GITHUB_USERNAME"
```

If no value can be resolved, stop and tell the user to set `git config --global github.user`.

### Docker
Run `docker info` to check if Docker is running. If the command fails:

1. Start Docker Desktop:
   ```powershell
   Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe"
   ```
2. Poll `docker info` every 5 seconds for up to 60 seconds. Print a waiting message each poll. If Docker is not ready after 60 seconds, stop and report the error.

### Azure CLI
Run `az version` to confirm `az` is installed. If it fails, stop and tell the user to install the Azure CLI.

### SWA CLI
Run `swa --version` to confirm `swa` is installed. If it fails, stop and tell the user to install the SWA CLI (`npm install -g @azure/static-web-apps-cli`).

## Step 2: Build & push the backend Docker image

Navigate to the `src\` directory and build the image tagged for GHCR:

```powershell
cd C:\Users\brand\dev\rssreader\rss-reader\src
docker build -t ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest -f Server/Dockerfile .
```

If the build fails, stop and report the error.

Then push the image:

```powershell
docker push ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest
```

If the push fails, it may mean the user is not logged in to GHCR. Remind them to run:
```powershell
echo $env:GITHUB_PAT | docker login ghcr.io -u $env:GITHUB_USERNAME --password-stdin
```

## Step 3: Update the Azure Container App

Update the running container app to use the new image:

```powershell
az containerapp update `
  --name rss-reader-api `
  --resource-group rss-container-rg `
  --image ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest
```

If this fails, check that the user is logged in to Azure (`az login`) and that the container app `rss-reader-api` exists in the `rss-container-rg` resource group.

## Step 4: Build & deploy the frontend

Navigate to the repository root and build the SWA frontend:

```powershell
cd C:\Users\brand\dev\rssreader\rss-reader
swa build
```

Then deploy to production:

```powershell
swa deploy --env production
```

If `swa deploy` fails with an authentication error, the user may need to run `swa login` first.

## Final summary

Report to the user:
- ✅ Backend image built and pushed: `ghcr.io/$GITHUB_USERNAME/rss-reader-api:latest`
- ✅ Azure Container App updated: `rss-reader-api`
- ✅ Frontend deployed to SWA production environment
