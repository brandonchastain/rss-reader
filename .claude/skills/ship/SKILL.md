---
name: ship
description: Take a change all the way to production for the rss-reader app via the full dev loop — git worktree, develop, build/test, PR, admin squash-merge, deploy, validate. Use when the user says "ship it", "run the dev loop", "do the full loop", "develop/test/merge/deploy/validate", or asks to implement-and-deploy a change to prod. Handles frontend-only (Azure Static Web Apps) and backend (Docker → GHCR → Azure Container Apps) deploys.
---

# Ship a change (rss-reader dev loop)

Take a change from idea to validated-in-production. Work one change per loop. Do NOT
skip steps; if a step can't complete, stop and report rather than faking success.

## ⛔ Security rules (never violate)
- Never echo, log, print, or `Write-Host` a token/password/secret. Pipe secrets via
  `--password-stdin` or use env vars the user already set (`$env:GITHUB_PAT`).
- Use the `gh` CLI for all GitHub operations; never extract/print credentials.
- If auth is missing and no safe method exists, stop and ask the user to run it.

## Step 1 — Work on a git worktree (not the main checkout)
The user requires change work on a separate worktree.
```bash
git worktree add -b <type>/<slug> ../rss-reader-<slug> main
```
`<type>` = feat | fix | chore. Do all edits under that worktree path.

## Step 2 — Develop
Make the change. Match the surrounding code's style, naming, and comment density.
Read files before editing. Reference `file:line` in notes.

## Step 3 — Build & test (in the worktree)
Detect scope from what you changed:
- **Backend** (`src/Server`, `src/Shared`):
  `dotnet build src/Server/Server.csproj -c Debug`
  `dotnet test test/SerializerTests/SerializerTests.csproj`  (all tests must pass)
  Add focused tests for new logic. The test project uses MSTest + Moq; inject a stub
  `HttpMessageHandler` to test feed-fetch behavior deterministically offline.
- **Frontend** (`src/WasmApp`):
  `dotnet build src/WasmApp/WasmApp.csproj -c Debug`
  Optional render check: write `.claude/launch.json` in the MAIN repo pointing
  `dotnet run --project <worktree>/src/WasmApp/WasmApp.csproj` at port 8443, then use the
  preview tools. Note: without the local API, the timeline shows "Failed to fetch" — that's
  expected; theme/anonymous pages still render. Auth-gated, audio, or post-dependent behavior
  can't be validated headlessly — hand those to the user.

Before committing, check for line-ending churn: `git diff --cached --stat` vs
`git diff --cached -w --stat`. Files in this repo are LF in git but check out as CRLF
(autocrlf); a noisy whole-file diff usually means an edit normalized a mixed-EOL file —
the `-w` diff shows the real change.

## Step 4 — Commit & push
```bash
git add -A
git commit -F - <<'EOF'
<type>: <subject>

<body explaining what and why>

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
git push -u origin <type>/<slug>
```

## Step 5 — Open the PR
```bash
gh pr create --base main --head <type>/<slug> --title "<title>" --body "<body>"
```
End the PR body with the Claude Code generation footer.

## Step 6 — Merge (admin squash)
`main` has branch protection, so a normal merge is rejected. The user owns the repo and
authorizes admin merges for this loop:
```bash
gh pr merge <number> --squash --admin
```
Do NOT pass `--delete-branch` from inside the worktree — it tries to check out `main`,
which is already checked out in the primary repo, and fails. Delete the branch in Step 7.
Verify: `gh pr view <number> --json state,mergeCommit --jq '{state, mergeCommit: .mergeCommit.oid}'`.

## Step 7 — Update main & clean up (run from the MAIN checkout)
```bash
git checkout main && git pull origin main
git worktree remove ../rss-reader-<slug> --force
git branch -D <type>/<slug>
git worktree prune
```

## Step 8 — Deploy to production
Pick the smallest correct deploy for what changed. `ghUser` = `brandonchastain`
(from the git remote). See `DEPLOY.md` and `.github/skills/deploy/` for canonical commands.

**Frontend-only** (only `src/WasmApp` changed) — SWA only:
```powershell
fnm env --use-on-cd --shell powershell | Out-String | Invoke-Expression; fnm use 20
swa build
swa deploy --env production
```

**Backend changed** (`src/Server`/`src/Shared`) — Docker → GHCR → ACA (do frontend too only if it also changed):
```powershell
# GHCR login (token via stdin — never printed)
$env:GITHUB_PAT | docker login ghcr.io -u brandonchastain --password-stdin
docker build -t "ghcr.io/brandonchastain/rss-reader-api:latest" -f src/Server/Dockerfile .
docker push "ghcr.io/brandonchastain/rss-reader-api:latest"
az containerapp update --name rss-reader-api --resource-group rss-container-rg `
  --image "ghcr.io/brandonchastain/rss-reader-api:latest" `
  --revision-suffix "deploy$(Get-Date -Format 'yyyyMMddHHmm')"   # unique suffix forces pickup
```
If `docker info` fails (daemon down), the GUI must elevate to start the privileged
`com.docker.service`. Launch it elevated so the user gets a UAC prompt, then poll:
```powershell
Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe" -Verb RunAs
# poll `docker info` until exit code 0
```
(A stale `%LOCALAPPDATA%\Docker\run\dockerInference` socket from an unclean shutdown can
make Docker show an Inference-manager error dialog; the core engine/BuildKit still work.)

## Step 9 — Validate
- **Backend**: poll `https://rss.brandonchastain.com/api/healthz` until HTTP 200 with
  `writer.status=healthy`. Confirm the new revision is active at 100% traffic:
  `az containerapp revision list --name rss-reader-api --resource-group rss-container-rg -o table`.
  A healthy writer also confirms additive schema (`CREATE TABLE IF NOT EXISTS`) applied cleanly.
- **Frontend**: confirm `https://rss.brandonchastain.com/` returns 200. For asset-level
  proof, fetch the changed file (e.g. `css/app.css`, `app.js`) and grep for the new code;
  for compiled WASM/auth-gated/audio behavior, hand the check to the user (note that a hard
  refresh may be needed to bust the Blazor service-worker cache).

## Conventions
- Schema changes must be transitionless: additive `CREATE TABLE IF NOT EXISTS` only, no
  destructive ALTER/DROP — the DB is live and Litestream-replicated, and rollback must be safe.
- The app runs on ACA with `minReplicas: 0` (scale-to-zero, HTTP scaler) and `maxReplicas: 1`:
  background work only runs while a replica is up, and there's exactly one scheduler instance.
- See [[use-worktree-for-changes]].
