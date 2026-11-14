---
name: ship
description: Take a change all the way to production for the rss-reader app via the full dev loop — git worktree, develop, build/test, PR, admin squash-merge, deploy, validate. Use when the user says "ship it", "run the dev loop", "do the full loop", "develop/test/merge/deploy/validate", or asks to implement-and-deploy a change to prod. Merging to main deploys through CI.
---

# Ship a change

Work on one change per loop. If a step can't complete, stop and report it. Never fake
success. Use `gh` for GitHub, and never print a token or secret.

## 1. Worktree
Do the work on a separate worktree, not the main checkout:
```bash
git worktree add -b <feat|fix|chore>/<slug> ../rss-reader-<slug> main
```

## 2. Develop and test
Match the surrounding style. Then run the checks for what you changed:
- **Backend:** `dotnet test test/RssReaderTests/RssReaderTests.csproj --filter "TestCategory!=Network"`.
  Add focused tests. Test feed fetching offline with a stub `HttpMessageHandler`.
- **Frontend:** `dotnet build src/WasmApp/WasmApp.csproj -c Debug`. Then check the UI
  in the browser with `cd test/e2e && npm test` and the `run-locally` skill. Hand any
  check you can't run headless (real sign-in, audio) to the user.

Before you commit, compare `git diff --cached --stat` with `git diff --cached -w --stat`.
Files check out as CRLF, so a whole-file diff usually means line endings changed.

## 3. Commit, push, PR
Use conventional commits (`type(scope): subject`), then:
```bash
git push -u origin <branch>
gh pr create --base main --title "<title>" --body "<body>"
gh pr checks <number> --watch
```

## 4. Merge (this deploys production)
A "ship it" from the user covers the merge. If they didn't ask to ship, ask first. Then:
```bash
gh pr merge <number> --squash --admin
```
Don't pass `--delete-branch` from inside the worktree: it tries to check out `main`,
which the main checkout already has. Clean up from the main checkout instead:
```bash
git checkout main && git pull
git worktree remove ../rss-reader-<slug> --force && git branch -D <branch> && git worktree prune
```

## 5. Watch the deploy
The merge triggers `.github/workflows/deploy.yml`. See [DEPLOY.md](../../../DEPLOY.md).
Never deploy by hand.
```bash
gh run list --workflow deploy.yml --branch main --limit 1 --json databaseId,headSha,status
gh run watch <id> --exit-status
```
If it fails, read the logs with `gh run view <id> --log-failed`. Fix it forward with a
new loop.

## 6. Validate
- **API:** `https://api.rss.brandonchastain.com/api/healthz` returns 200 with
  `"status":"healthy"` and no `"phase":"seeding"`.
- **Site:** `https://rss.brandonchastain.com/` returns 200. For a frontend change, fetch
  the changed asset and grep it for the new code.
