# Skill: git-pr

Create a feature branch, commit changes, push, and open a pull request — safely, without leaking credentials.

---

## ⛔ Credential safety rules — read this first

These rules were learned the hard way. Violating them leaks tokens into session logs.

| ❌ Never do this | ✅ Do this instead |
|---|---|
| `Invoke-RestMethod` / `curl` with `Authorization: Bearer <token>` | Use the `github` MCP server tool |
| `git credential fill` (token appears in stdout — **this is how leaks happen**) | Do not extract tokens at all; MCP tools handle auth internally |
| Capturing `git credential fill` output in any variable | Same — never run this command |
| `cmdkey /list` to find tokens | Do not enumerate credentials |
| `gh pr create` (gh CLI is not installed on this machine) | Use the `github` MCP server tool |
| `git add .` without checking status first | Always `git status` first; stage individual files |

**There is no acceptable fallback.** If the GitHub MCP `create_pull_request` tool is unavailable, the only options are (1) ask the user to restart the session so the MCP server reconnects, or (2) provide the user the branch URL and ask them to open the PR manually at github.com. **Never attempt to create the PR via the REST API.**

---

## Prerequisites

Before starting, verify:

1. **Docker is running** — the GitHub MCP server runs as a container.
2. **`github` MCP server is connected** — you should have `create_pull_request` available as a tool.  
   If it's not available: stop, ask the user to restart their Copilot CLI session, then retry.  
   **Do not fall back to REST API calls.**
3. **`GITHUB_TOKEN` env var is set** (for Copilot CLI context). See Setup below.

### One-time setup: GITHUB_TOKEN for the Copilot CLI

The Copilot CLI stores the GitHub token in Windows Credential Manager during login. To expose it as an environment variable **without printing it to the console**, add this to `$PROFILE` (e.g. `~\Documents\PowerShell\Microsoft.PowerShell_profile.ps1`):

```powershell
# Silently load GitHub token from Windows Credential Manager into env
# Uses git's credential helper so the token is never echoed to stdout
$_ghCred = [System.Text.Encoding]::UTF8.GetString(
    [System.Convert]::FromBase64String(
        ([System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR(
                (Get-StoredCredential -Target 'git:https://github.com' -AsCredentialObject -ErrorAction SilentlyContinue)?.Password
            )
        ) ?? '')
    ) 2>$null
) 2>$null
# Simpler alternative: set it manually once per session
# $env:GITHUB_TOKEN = Read-Host "GitHub token" -AsSecureString | ... (use a password manager)
```

**Simplest approach** — set it manually once and add to your shell profile:
```powershell
# Run once in terminal (token is typed, not echoed if using Read-Host -AsSecureString)
$env:GITHUB_TOKEN = (Read-Host "GitHub token" -AsSecureString | `
    [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($_)))
```

For VS Code context: `${input:github-token}` is used in `.vscode/mcp.json` — VS Code prompts once and stores the token in the OS keychain.

---

## Steps

### Step 1 — Check current state

```powershell
cd C:\Users\brand\dev\rssreader\rss-reader
git --no-pager status
git --no-pager branch
```

Note which branch you're on and what files are modified/untracked.

### Step 2 — Clean up artifact files before stashing

Look at `git status` output for untracked files that shouldn't be committed (screenshots, logs, temp files). If found, add them to `.gitignore` first:

```powershell
# Example: root-level PNGs from Playwright screenshots
# Add /*.png to .gitignore before stashing
git --no-pager status  # identify untracked junk
# Edit .gitignore to exclude them
# Then stage the .gitignore change along with your real changes
```

Common patterns to ignore:
- `/*.png` — root-level screenshots (Playwright saves these to cwd)
- `/*.jpg`, `/*.jpeg`
- `/tmp-*`

### Step 3 — Stash all working changes with a descriptive message

```powershell
git stash push -m "<type>: <brief description of what's stashed>"
git stash list   # confirm stash@{0} exists
```

> Stash first, then create the branch — this ensures `main` is clean when you branch off it.

### Step 4 — Create the feature branch from a fresh main

```powershell
git checkout main
git pull origin main          # make sure main is up to date
git checkout -b dev/bc/<slug>
```

**Branch naming convention:** `dev/bc/<slug>` where `<slug>` is a short kebab-case description:
- `fix-infinite-scroll-collapse`
- `add-dark-mode`
- `refactor-feed-repository`

Never reuse an existing branch. Check with `git --no-pager branch -a | Select-String dev/bc` if unsure.

### Step 5 — Pop the stash

```powershell
git stash pop
git --no-pager diff --stat   # confirm all changes are back
git --no-pager status        # verify nothing unexpected appeared
```

### Step 6 — Stage only the relevant files

**Never `git add .`** — always inspect first and add explicitly:

```powershell
git --no-pager status
# Review the list carefully. Untracked artifact files should already be in .gitignore from Step 2.
git add src/WasmApp/Pages/Detail/PostDetail.razor
git add src/WasmApp/wwwroot/app.js
git add .gitignore   # if you modified it in Step 2
# ... add each file individually
```

### Step 7 — Commit with a conventional commit message

```powershell
git commit -m "<type>: <short imperative summary under 72 chars>

<blank line>
<optional body: what changed, why, any important context>
<wrap at 72 chars>

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

**Commit type prefixes:**
| Prefix | When to use |
|--------|-------------|
| `fix:` | Bug fix |
| `feat:` | New feature |
| `chore:` | Tooling, config, .gitignore, dependencies |
| `refactor:` | Code restructure, no behaviour change |
| `style:` | CSS / formatting only |
| `docs:` | Documentation only |
| `test:` | Tests only |

> Always include the `Co-authored-by: Copilot` trailer. It's required by convention for AI-assisted commits.

> ⚠️ PowerShell does **not** support bash heredoc (`<<EOF`). Multi-line commit messages must be written as a single string with embedded newlines (`\n` or backtick-n in PowerShell), or by using a temp file and `git commit -F`.

### Step 8 — Push the branch and set upstream tracking

```powershell
git push -u origin dev/bc/<slug>
```

The `-u` flag sets the upstream so future `git push` / `git pull` work without arguments.

### Step 9 — Create the pull request via the GitHub MCP server

Use the `create_pull_request` MCP tool. **Never use `Invoke-RestMethod` or any HTTP client with a bearer token.**

Parameters:
- **owner**: `brandonchastain`
- **repo**: `rss-reader`
- **head**: `dev/bc/<slug>`
- **base**: `main`
- **title**: same as the commit subject line (without the type prefix if it reads awkwardly as a title)
- **body**: see template below
- **draft**: `false` (unless explicitly asked to create a draft)

#### PR body template

```markdown
## Problem

<1–3 sentences describing the bug, gap, or improvement this PR addresses.
Be specific: what was happening, under what conditions, what was the impact.>

## Changes

### `<filename or component>`
<What changed and why. One sub-heading per logical group of files.>

### `<filename or component>`
<...>

## Validation

<How was this tested? Examples:>
- Playwright: collapsed a long post on desktop (1280px) and mobile (375px) — `delta = 0` spurious loads ✅
- `dotnet test` — all N tests pass ✅
- Manual: navigated to /timeline, expanded and collapsed posts, confirmed scroll position ✅
```

### Step 10 — Confirm and report

After the MCP tool responds:
- Report the PR URL to the user
- Confirm the branch name and PR number

---

## Troubleshooting

### `create_pull_request` tool not available
The `github` MCP server is not connected. Two options — pick one:
1. Ask the user to restart their Copilot CLI session (the MCP server reconnects on session init), then retry Step 9.
2. Provide the user with the push URL (e.g. `https://github.com/brandonchastain/rss-reader/pull/new/<branch>`) and ask them to open the PR manually via the GitHub web UI.

**Do NOT fall back to `Invoke-RestMethod`, `curl`, or any HTTP client. Do NOT run `git credential fill` to obtain a token. There is no REST API fallback.**

### `git stash pop` conflicts
Resolve conflicts manually, then `git add <file>` and `git stash drop` (do not `git stash pop` again).

### Push rejected (non-fast-forward)
The branch already exists on the remote with different commits. Use `git push --force-with-lease` only if you are sure the remote branch is your own work and no one else has pushed to it.

### Branch already exists locally
```powershell
git branch -D dev/bc/<slug>        # delete local
git push origin --delete dev/bc/<slug>   # delete remote (if needed)
```
Then restart from Step 4.

