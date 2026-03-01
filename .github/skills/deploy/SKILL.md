---
name: deploy
description: Deploy the RSS Reader app to production. Use this when asked to deploy, push to production, or release the app. Builds and pushes the backend Docker image, updates the Azure Container App, then builds and deploys the SWA frontend. Does NOT run Bicep templates or change infrastructure.
---

Deploy the RSS Reader app to production by following these steps in order.

## Step 0: Confirm with user before proceeding

**⛔ STOP — do not proceed without explicit user confirmation.**

Before doing anything else, use the `ask_user` tool to ask:

> "Ready to deploy to production? This will push a new Docker image and update the live Azure Container App and SWA at https://rss.brandonchastain.com."

Wait for the user to confirm. If they say anything other than a clear yes, abort the deployment and report that it was cancelled.

Only continue to Step 1 after receiving explicit confirmation.

## Step 1: Check prerequisites

### Node version
Initialize fnm and switch to Node 20 before running any other commands:

```powershell
fnm env --use-on-cd --shell powershell | Out-String | Invoke-Expression
fnm use 20
```

### GITHUB_USERNAME
Resolve the GitHub username from the git remote URL (primary) or git config (fallback):

```powershell
$remoteUrl = git remote get-url origin 2>$null
if ($remoteUrl -match 'github\.com[:/]([^/]+)/') {
    $ghUser = $Matches[1]
} else {
    $ghUser = git config github.user
}
if (-not $ghUser) {
    Write-Error "Could not determine GitHub username. Set it with: git config --global github.user 'your-github-username'"
    exit 1
}
Write-Host "Using GitHub username: $ghUser"
```

**Important:** Use the local `$ghUser` variable (not `$env:GITHUB_USERNAME`) within each command block, because env vars do not persist across separate tool calls. Every subsequent step that needs the username must resolve it in the same command invocation using the same pattern above.

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

Navigate to the `src\` directory and build the image tagged for GHCR. Resolve `$ghUser` inline in the same command:

```powershell
$remoteUrl = git remote get-url origin 2>$null
if ($remoteUrl -match 'github\.com[:/]([^/]+)/') { $ghUser = $Matches[1] } else { $ghUser = git config github.user }
cd C:\Users\brand\dev\rssreader\rss-reader\src
docker build -t "ghcr.io/$ghUser/rss-reader-api:latest" -f Server/Dockerfile .
```

If the build fails, stop and report the error.

Then push the image (resolve `$ghUser` inline again in the same command):

```powershell
$remoteUrl = git remote get-url origin 2>$null
if ($remoteUrl -match 'github\.com[:/]([^/]+)/') { $ghUser = $Matches[1] } else { $ghUser = git config github.user }
docker push "ghcr.io/$ghUser/rss-reader-api:latest"
```

If the push fails, it may mean the user is not logged in to GHCR. Remind them to run:
```powershell
$remoteUrl = git remote get-url origin 2>$null
if ($remoteUrl -match 'github\.com[:/]([^/]+)/') { $ghUser = $Matches[1] } else { $ghUser = git config github.user }
echo $env:GITHUB_PAT | docker login ghcr.io -u $ghUser --password-stdin
```

## Step 3: Update the Azure Container App

Update the running container app to use the new image (resolve `$ghUser` inline):

```powershell
$remoteUrl = git remote get-url origin 2>$null
if ($remoteUrl -match 'github\.com[:/]([^/]+)/') { $ghUser = $Matches[1] } else { $ghUser = git config github.user }
az containerapp update `
  --name rss-reader-api `
  --resource-group rss-container-rg `
  --image "ghcr.io/$ghUser/rss-reader-api:latest"
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

## Step 5: Validate deployment

### 5a: Azure resource health checks (MCP)

Check the Container App health status using the Azure MCP tool `resourcehealth availability-status get` with resource group `rss-container-rg` and resource name `rss-reader-api`. Report the availability state (should be `Available`).

Also confirm the latest revision is active and receiving traffic:

```powershell
az containerapp revision list --name rss-reader-api --resource-group rss-container-rg --output table
```

The most recent revision should have `ACTIVE` state and a traffic weight of `100`.

### 5b: Browser smoke test (Playwright)

First check whether Playwright MCP tools (e.g. `browser_navigate`, `browser_snapshot`) are available.

**If Playwright tools are NOT available:** skip this sub-step and note it in the summary.

**If Playwright tools ARE available:**

1. Navigate to production: `browser_navigate(url: "https://rss.brandonchastain.com")`

2. Unregister the Blazor service worker cache and reload so the freshly deployed assets are served:
   ```js
   browser_evaluate(function: `async () => {
     const regs = await navigator.serviceWorker.getRegistrations();
     for (const reg of regs) await reg.unregister();
     return regs.length + ' service worker(s) unregistered';
   }`)
   ```
   Then `browser_navigate(url: "https://rss.brandonchastain.com")` again.

3. Take a snapshot: `browser_snapshot()`. Confirm the homepage loads (look for the app title / login button).

4. Check whether the user is already logged in by looking for auth-gated page content (e.g. the Feeds or Timeline nav links are visible and accessible). If the user appears to be logged out, ask them to log in:

   > The app is showing the logged-out homepage. Please log in via the browser and let me know when you're done, then I'll continue the smoke test.

5. Once logged in (or if already logged in), run these basic scenarios:
   - Navigate to `/feeds` — confirm the feeds list page loads without errors.
   - Navigate to `/timeline` — confirm the timeline page loads and shows content (or an empty state, not a crash).
   - Take a screenshot: `browser_take_screenshot(type: "png")` for visual confirmation.

6. Report what was observed: page titles, any visible errors or blank screens, HTTP failures in the console.

## Final summary

Report to the user:
- ✅ Backend image built and pushed: `ghcr.io/<username>/rss-reader-api:latest`
- ✅ Azure Container App updated: `rss-reader-api`
- ✅ Frontend deployed to SWA production environment
- ✅ Azure resource health: `Available` (or report the actual status)
- ✅ Browser smoke test passed (or describe any issues found)
