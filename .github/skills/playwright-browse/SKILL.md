---
name: playwright-browse
description: Start a Playwright browser session to interactively browse a URL. Use this when asked to browse a site, navigate a page, check what a page looks like, click through the app, or take a screenshot. Requires the Playwright MCP server to be connected.
---

Start an interactive Playwright browsing session by following these steps.

## Step 1: Verify the Playwright MCP tools are available

Check whether Playwright MCP tools (e.g. `browser_navigate`, `browser_snapshot`, `browser_click`) are listed in your available tools.

**If the tools ARE available:** proceed to Step 2.

**If the tools are NOT available:**
Stop and tell the user:

> ⚠️ The Playwright MCP server is not connected. Please restart the GitHub Copilot CLI (close and reopen it), then try again. The server is configured in `.vscode/mcp.json` and connects at startup.

If after restarting the tools still aren't available, check that Firefox is installed for the correct Playwright version used by the MCP server:

```powershell
$npxCache = "$env:LOCALAPPDATA\npm-cache\_npx"
$mcpEntry = Get-ChildItem $npxCache -Recurse -Filter "package.json" -ErrorAction SilentlyContinue |
    Where-Object { (Get-Content $_.FullName -Raw 2>$null) -match '"@playwright/mcp"' } |
    Select-Object -First 1
$mcpDir = $mcpEntry.DirectoryName
node "$mcpDir\node_modules\playwright\cli.js" install firefox
```

Then restart the CLI again.

## Step 2: Determine the target URL

If the user specified a URL, use it. If not, default to the production site: **https://rss.brandonchastain.com**

To browse the local dev stack instead, use **http://localhost:4280** (requires the `run-locally` skill to have been run first).

## Step 3: Navigate to the URL

Use `browser_navigate` to open the target URL:

```
browser_navigate(url: "<target URL>")
```

Wait for the page to finish loading before proceeding.

## Step 4: Take a snapshot to see the page

Use `browser_snapshot` to get the current accessibility tree / DOM content of the page. This tells you what's visible, what links exist, what buttons are present, and what text is on the page.

```
browser_snapshot()
```

Report what you observe: page title, main content, visible navigation links, any buttons or CTAs.

## Step 5: Wait for the user to login (if necessary)

If the user is not logged in, prompt them to log in before proceeding. You can detect this by looking for common login elements (e.g. a link to `/.auth/login/aad` or a form with username/password fields) in the snapshot.

## Step 6: Continue interacting as requested

Based on the user's goal, use the available Playwright tools to navigate, click, fill forms, or screenshot:

| Goal | Tool |
|------|------|
| Navigate to a new URL or click a link | `browser_navigate` or `browser_click` |
| Read current page content | `browser_snapshot` |
| Take a screenshot | `browser_take_screenshot` |
| Fill in a text field | `browser_type` |
| Scroll the page | `browser_scroll` |
| Go back | `browser_navigate` with the previous URL |

## Notes for the RSS Reader app

- **Public pages** (no login required): `/` (homepage), `/privacy`
- **Auth-gated pages**: `/feeds`, `/posts`, `/timeline`, `/search` — these redirect to `/.auth/login/aad` (Azure AD login) for unauthenticated users
- The login button on the homepage is an `<a>` tag pointing to `/.auth/login/aad`
- If running locally with `run-locally`, authentication is bypassed via `RssAppConfig__IsTestUserEnabled=true` and the test user `testuser2` is used automatically
