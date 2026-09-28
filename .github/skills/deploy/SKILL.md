---
name: deploy
description: Deploy the RSS Reader app to production. Use this when asked to deploy, push to production, or release the app. Triggers the Deploy GitHub Actions workflow (which builds and pushes the backend image, deploys the Bicep template, and publishes the SWA frontend), waits for it, then validates the live site. Never deploys from the local machine.
---

Deploy the RSS Reader app to production by following these steps in order.

## How production is deployed

Everything ships through `.github/workflows/deploy.yml`. On every push to `main` (and on
`workflow_dispatch`) it runs the tests, builds the backend image and pushes it to the
public GHCR package with the workflow's own `GITHUB_TOKEN`, deploys `infrastructure/main.bicep`
with that image tag, and publishes the Blazor frontend + Functions API proxy to Static Web
Apps. Azure auth is OIDC through a managed identity; no personal access token, registry
password, or SWA deployment token lives on any laptop.

**Never deploy by hand.** `docker push` + `az containerapp update` replaces the container
app's scale block wholesale and drops settings the template declares; that drift caused a
real outage (cooldownPeriod fell to 10s and KEDA killed every new revision mid-boot). The
template is the only thing that writes container app config.

## ⛔ Security rules — NEVER violate these

1. **Never run `git credential fill`**, `git credential approve`, `cmdkey`, or any other command that reads credentials from the system credential store and prints them to stdout.
2. **Never print, log, echo, or `Write-Host` the value of any token, password, or secret.**
3. Use the `gh` CLI for all GitHub operations; it handles auth internally. If `gh auth status` fails, **stop and ask the user** to run `gh auth login` themselves, then continue.

## Step 0: Pre-deploy checks

### 0a: Confirm with user

**⛔ STOP — do not proceed without explicit user confirmation.**

Before doing anything else, use the `ask_user` tool to ask:

> "Ready to deploy to production? This will run the Deploy workflow, which pushes a new backend image and updates the live Azure Container App and SWA at https://rss.brandonchastain.com."

Wait for the user to confirm. If they say anything other than a clear yes, abort the deployment and report that it was cancelled.

### 0b: Ensure changes are merged to main

**⛔ Production deploys build from `main` on GitHub.** Local, uncommitted, or unmerged changes are not deployed.

1. Run `git --no-pager status` and `git --no-pager log --oneline -1` to check the current state.
2. **If there are uncommitted changes** or the current branch is not `main`, stop and tell the user:
   > "There are uncommitted changes (or you're not on main). I need to commit these to a branch, push, create a PR, and merge before deploying. Want me to proceed?"
   Wait for confirmation, then create a feature branch, commit, push, open a PR with `gh pr create`, and squash-merge it. Merging to `main` triggers the deploy automatically; skip to Step 2.
3. **If `main` is clean**, make sure the local branch matches the remote:
   ```powershell
   git fetch origin
   git --no-pager log --oneline -1 origin/main
   ```

## Step 1: Trigger the Deploy workflow

If Step 0b merged a PR, the push to `main` already started a run; do not start a second one.
Otherwise (re-deploying the current `main`, e.g. to pick up an infrastructure change or roll
a fresh revision), dispatch it:

```powershell
gh workflow run deploy.yml --ref main
```

## Step 2: Wait for the run

Find the newest run on `main` and watch it to completion:

```powershell
$run = gh run list --workflow deploy.yml --branch main --limit 1 --json databaseId,headSha,status --jq '.[0]'
$run
gh run watch ($run | ConvertFrom-Json).databaseId --exit-status
```

Confirm `headSha` is the commit you expect to deploy. If the run fails, show the failing
job with `gh run view <databaseId> --log-failed`, report the error, and stop. Fix forward
with a new PR; never patch production directly.

## Step 3: Validate deployment

### 3a: Backend health

Poll until healthy (the app scales to zero, so the first request may take ~15s):

```powershell
Invoke-WebRequest -UseBasicParsing https://rss.brandonchastain.com/api/healthz | Select-Object -ExpandProperty Content
```

Expect HTTP 200 with `writer.status = "healthy"`. Then confirm the new revision owns the traffic:

```powershell
az containerapp revision list --name rss-reader-api --resource-group rss-container-rg --query "[?properties.active].{name:name,traffic:properties.trafficWeight,state:properties.runningState,health:properties.healthState}" -o table
```

The single active revision should have traffic `100` and health `Healthy`. Optionally check
Azure resource health with the Azure MCP tool `resourcehealth availability-status get`
(resource group `rss-container-rg`, resource `rss-reader-api`); expect `Available`.

### 3b: Browser smoke test (Playwright)

First, run the Firefox profile recovery procedure to clear any stale locks:

```powershell
$staleFirefox = Get-Process -Name firefox -ErrorAction SilentlyContinue
if ($staleFirefox) {
    foreach ($proc in $staleFirefox) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}
$profileDir = "$env:LOCALAPPDATA\ms-playwright\mcp-firefox"
if (Test-Path $profileDir) {
    Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
}
```

Then check whether Playwright MCP tools (e.g. `browser_navigate`, `browser_snapshot`) are available.

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
   - **Content display check**: Click a post thumbnail to expand it. Verify that article content text is visible in the expanded area (not just "Published on" date and action buttons).
   - Take a screenshot: `browser_take_screenshot(type: "png")` for visual confirmation.

6. Report what was observed: page titles, any visible errors or blank screens, HTTP failures in the console.

## Final summary

Report to the user:
- ✅ Deploy workflow run `<databaseId>` succeeded for commit `<headSha>` (link: `gh run view <databaseId> --web`)
- ✅ `/api/healthz` returned 200 with the writer healthy; active revision `<name>` at 100% traffic
- ✅ Frontend live at https://rss.brandonchastain.com
- ✅ Browser smoke test passed (or describe any issues found)
