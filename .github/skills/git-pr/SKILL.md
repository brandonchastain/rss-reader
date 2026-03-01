# Skill: git-pr

Create a feature branch, commit staged changes, push, and open a pull request — all without leaking credentials.

## Prerequisites

- `GITHUB_TOKEN` env var set for the Copilot CLI (see Setup below), **OR** VS Code's `${input:github-token}` prompt will handle it interactively.
- The `github` MCP server must be connected (configured in `.vscode/mcp.json` and `~/.copilot/mcp.json`).
- Docker must be running (the GitHub MCP server runs in a container).

### Setup: setting GITHUB_TOKEN for the Copilot CLI

The token is already stored in Windows Credential Manager after `gh auth login` or Copilot CLI login. To expose it as an env var for the current session:

```powershell
# One-time: add to your PowerShell profile so it persists
$token = ("protocol=https`nhost=github.com`n`n" | git credential fill) -match 'password=(.+)' | Out-Null; $Matches[1]
$env:GITHUB_TOKEN = $token
```

Or add this to your `$PROFILE`:
```powershell
$credResult = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
if ($credResult -match 'password=(.+)') { $env:GITHUB_TOKEN = $Matches[1] }
```

---

## Steps

### Step 1 — Determine the branch name

Derive the branch name from the task description using the pattern `dev/bc/<slug>` where `<slug>` is a short kebab-case summary of the change (e.g. `fix-infinite-scroll-collapse`, `add-dark-mode`, `update-nav-layout`).

Ask if unsure. Never reuse an existing branch name.

### Step 2 — Stash any unstaged changes

```powershell
git stash push -m "<brief description>"
git stash list   # confirm stash was created
```

### Step 3 — Create the feature branch from main

```powershell
git checkout main
git pull origin main
git checkout -b dev/bc/<slug>
```

### Step 4 — Pop the stash

```powershell
git stash pop
git --no-pager diff --stat   # confirm changes are back
```

### Step 5 — Stage and commit

Stage only the relevant files (never use `git add .` blindly — check for unintended files first):

```powershell
git --no-pager status
git add <file1> <file2> ...
git commit -m "<type>: <short summary>

<optional longer description of what changed and why>

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

Commit message format:
- `fix:` — bug fix
- `feat:` — new feature
- `chore:` — tooling, config, dependencies
- `refactor:` — code restructure without behaviour change
- `style:` — CSS / formatting only

### Step 6 — Push the branch

```powershell
git push -u origin dev/bc/<slug>
```

### Step 7 — Create the pull request via GitHub MCP server

Use the `github` MCP server tool — **never use `Invoke-RestMethod` or `curl` directly with a token**.

Call the MCP tool to create a pull request:
- **owner**: `brandonchastain`
- **repo**: `rss-reader`
- **head**: `dev/bc/<slug>`
- **base**: `main`
- **title**: same as the commit subject line
- **body**: markdown description with Problem, Changes, and Validation sections (see template below)

#### PR body template

```markdown
## Problem

<1–3 sentences describing the bug or gap this PR addresses.>

## Changes

<Bullet list or sub-headings describing each file changed and what was done.>

## Validation

<How was this tested? List any Playwright results, unit tests, or manual steps.>
```

### Step 8 — Confirm

Report the PR URL to the user.

---

## Notes

- The `github` MCP server requires Docker to be running.
- The MCP server authenticates via `GITHUB_TOKEN` (Copilot CLI) or `${input:github-token}` (VS Code) — the token is **never** written to any file or printed to the console.
- If the GitHub MCP `create_pull_request` tool is unavailable (server not connected), stop and ask the user to restart their Copilot CLI session so the MCP server can connect, then retry.
- Do **not** fall back to `Invoke-RestMethod` / `curl` with a bearer token — this leaks credentials into session logs.
