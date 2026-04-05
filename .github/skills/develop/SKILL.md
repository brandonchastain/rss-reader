---
name: develop
description: End-to-end development workflow using TDD. Use this when asked to implement a feature, fix a bug, or make any code change. Follows red/green TDD, builds, runs tests, validates locally with Playwright, creates and merges a PR, then deploys to production.
---

# Develop Skill — End-to-End TDD Workflow

Follow these phases **in order**. Each phase must complete before moving to the next.

## Phase 1: Plan

1. Understand the user's request. Ask clarifying questions if needed via `ask_user`.
2. Explore the relevant code using grep/glob/view to understand the current state.
3. Write a plan in `plan.md` covering:
   - Problem statement and proposed approach
   - Files to change
   - Key design decisions
4. Present the plan via `exit_plan_mode` for user approval.
5. After approval, insert todos into the SQL `todos` table with dependencies.

## Phase 2: Red — Write Failing Tests

1. Write tests **first** that assert the desired behavior.
2. Run tests to confirm they **fail** for the right reason:
   ```powershell
   dotnet test rss-reader.sln --verbosity quiet
   ```
3. If no testable behavior exists (pure UI change, config-only), skip to Phase 3.

## Phase 3: Green — Implement

1. Make the minimum changes to pass the failing tests.
2. Run tests again to confirm they **pass**:
   ```powershell
   dotnet test rss-reader.sln --verbosity quiet
   ```
3. If tests fail, iterate until green.

## Phase 4: Build & Test

1. Build the full solution:
   ```powershell
   dotnet build -c Debug rss-reader.sln --verbosity quiet
   ```
2. Run all tests:
   ```powershell
   dotnet test rss-reader.sln --verbosity quiet
   ```
3. Both must succeed with 0 errors before proceeding.

## Phase 5: Local Smoke Test

**Only for changes that affect the UI or API behavior.**

1. Start the local stack using the `run-locally` skill.
2. Clean up stale Firefox processes and profile directory before Playwright:
   ```powershell
   Get-Process -Name firefox -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
   if (Test-Path "$env:USERPROFILE\AppData\Local\mcp-firefox") { Remove-Item "$env:USERPROFILE\AppData\Local\mcp-firefox" -Recurse -Force -ErrorAction SilentlyContinue }
   ```
3. Use Playwright to browse `http://localhost:4280/`:
   - Navigate to `/` first (direct route navigation may cause content encoding errors with SWA CLI).
   - Wait for Blazor to load and redirect to `/timeline`.
   - Verify the changed behavior works as expected.
   - Take a screenshot for confirmation.
4. Stop the local stack using the `stop-local` skill.

### Local smoke test tips

- The local database starts empty on fresh containers. Add a test feed if needed:
  ```powershell
  $headers = @{"X-Gateway-Key"="testkey123"; "X-User-Id"="testuser2"}
  $feed = '{"Href":"https://feeds.arstechnica.com/arstechnica/index","Title":"Ars Technica","UserId":1}'
  Invoke-RestMethod -Uri "http://localhost:8080/api/feed" -Method Post -Headers $headers -Body $feed -ContentType "application/json"
  ```
- Auth is bypassed locally via `RssAppConfig__IsTestUserEnabled=true`.
- Click post **thumbnails** to expand (not title links, which navigate externally).

## Phase 6: Create PR

1. Create a branch, commit, and push:
   ```powershell
   git checkout -b <branch-name>
   git add <files>
   git commit -m "<descriptive message>

   Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
   git push -u origin <branch-name>
   ```
2. Create the PR:
   ```powershell
   gh pr create --title "<title>" --body "<description>" --base main --head <branch-name>
   ```

## Phase 7: Merge PR

```powershell
gh pr merge <number> --squash --delete-branch --admin
```

This squash-merges, deletes the branch, and pulls main locally.

## Phase 8: Deploy to Production

**⚠️ STOP: Ask the user for explicit confirmation before deploying.**

Use the `deploy` skill to deploy. The deploy skill handles:
- Building and pushing the Docker image
- Updating the Azure Container App
- Building and deploying the SWA frontend
- Running validation (Azure health check + Playwright smoke test)

## Phase 9: Production Smoke Test

After deployment, use the `playwright-browse` skill to verify on `https://rss.brandonchastain.com`:
1. Clear Blazor service worker cache.
2. Verify the changed behavior works in production.
3. Take a screenshot for confirmation.

---

## Quick Reference

| Phase | Key Command | Gate |
|-------|------------|------|
| Plan | `exit_plan_mode` | User approval |
| Red | `dotnet test` | Tests fail |
| Green | `dotnet test` | Tests pass |
| Build | `dotnet build && dotnet test` | 0 errors |
| Smoke | `run-locally` skill + Playwright | Visual confirmation |
| PR | `gh pr create` | PR created |
| Merge | `gh pr merge --squash --admin` | Merged |
| Deploy | `deploy` skill | **User confirmation required** |
| Prod test | `playwright-browse` skill | Visual confirmation |
