# Deploying to production

Production deploys from CI. Merging to `main` runs `.github/workflows/deploy.yml`, which
tests, builds and pushes the backend image, deploys `infrastructure/main.bicep` with that
image tag, and publishes the frontend to Static Web Apps. PRs that touch
`infrastructure/` get a template validate and what-if.

First-time environment buildout lives in [SELF-HOSTING.md](SELF-HOSTING.md#azure-first-time-buildout).
How the app keeps its data is in [ARCHITECTURE.md](ARCHITECTURE.md#backup--replication).

## Cheat sheet

```bash
# Re-deploy the current main without a code change, and wait for it
gh workflow run deploy.yml --ref main
gh run watch $(gh run list --workflow deploy.yml --branch main --limit 1 --json databaseId --jq '.[0].databaseId') --exit-status
```

Or run `.github/skills/deploy/deploy.ps1`, which does the same and then checks `/api/healthz`.

**Do not deploy by hand** (`docker push`, `az containerapp update`, `swa deploy`).
`az containerapp update` replaces the container app's scale block and drops settings the
template declares; that drift caused an outage. The template is the only writer of
container app config.

## Credentials (there are none on your machine)

- **GHCR:** the `ghcr.io/brandonchastain/rss-reader-api` package is public. CI pushes it
  with the workflow's `GITHUB_TOKEN` (`permissions: packages: write`); the Container App
  pulls it anonymously and stores no registry credential. No personal access token exists
  for this project, and none should be created. If the package ever becomes private, the
  next revision fails to pull (`ImagePullBackOff`, 403 from `ghcr.io/token`); the fix is
  to make it public again, not to add a PAT.
- **Azure:** CI logs in with OIDC through the `gh-actions-rss-reader` user-assigned
  managed identity. Repo secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
  `AZURE_SUBSCRIPTION_ID` hold the identifiers.
- **Login providers:** `AUTH_{GOOGLE,MICROSOFT,GITHUB}_CLIENT_{ID,SECRET}` repo secrets,
  passed to the template and stored as container app secrets. Rotate one by updating the
  repo secret and re-running the deploy.
- **SWA:** the deployment token is read by CI from `az staticwebapp secrets list` at run
  time and never stored.

## Changing infrastructure

Edit `infrastructure/main.bicepparam` (sizing, replica counts, read replicas) or
`main.bicep` and merge. Every parameter and env var is listed under
[Knobs](ARCHITECTURE.md#knobs). Cost consequences are noted there too; the always-on
replica and the 5-minute backup copy are the two lines that show up on the bill.

## Monitoring

```bash
# Follow container logs
az containerapp logs show --name rss-reader-api --resource-group rss-container-rg --follow

# Replica count (expect exactly 1: minReplicas is 1 and SQLite allows one writer)
az containerapp replica list --name rss-reader-api --resource-group rss-container-rg

# Running status
az containerapp show --name rss-reader-api --resource-group rss-container-rg --query properties.runningStatus
```

Logs land in the `rss-reader-logs` Log Analytics workspace at Warning and above.

## Sign-in smoke check

The Deploy workflow ends with `.github/scripts/smoke-prod.sh`, which needs only bash and
curl and no credentials. It cannot sign anyone in, but it checks every piece that broke or
could break silently: each configured provider's login redirects to the right authorize
endpoint with a client id, a state, PKCE and a correlation cookie; the provider's POST back
to `/auth/callback/<provider>` is not refused by the cross-site guard and a failed one
returns to the frontend (not the API host); writes from a foreign or `null` origin get 403;
mark-as-read has no GET route; CORS admits only the frontend; an anonymous `/auth/me` has no
principal; and the site is serving and pointed at this API. A failure fails the Frontend job
and prints `FAIL` lines naming the broken piece.

Run it by hand any time (for example after changing a provider registration):

```bash
bash .github/scripts/smoke-prod.sh
API=https://api.example.com SITE=https://app.example.com bash .github/scripts/smoke-prod.sh
```

It does not replace one real sign-in after changing auth code: open the site in a private
window, sign in with each provider, and sign out.

## Troubleshooting

**App refuses to boot after a deploy.** The app seeds `/tmp/storage.db` from
`/data/db/storage.db` and runs `PRAGMA quick_check` on it; a failed check stops the app
rather than let the backup service overwrite the share with bad data. Logs show
`PRAGMA quick_check failed on the seeded database`. Restore a known-good copy to the share and redeploy.

**New revision sits at "waiting for it to release" and the API answers 503.** The new
revision is waiting for the previous one's lock on the share (`/data/db/storage.db.lock`)
while that one writes its final DB copy on SIGTERM. Normal for up to a minute or two.
If the old replica died without releasing, the lock is taken over once its heartbeat
is older than `BackupLockStaleAfter` (3 min); nothing to do but wait.

**`FATAL: Parent of backup directory '/data' does not exist`.** The Azure Files volume
mount is missing from the revision. Check the template's `volumeMounts`; this is the
symptom of a hand-run `az containerapp update`.

**Share quota.** A backup cycle briefly holds the old copy and the new one, so the
`rss-data` share quota must exceed twice the database size. At 1 GB a 530 MB database
crossed that line on 2026-10-02 and the truncated copy was refused at boot. The template
sets 5 GB; raise it in `main.bicep` before the database approaches 2.5 GB.

**Verify the mount.**

```bash
az containerapp exec --name rss-reader-api --resource-group rss-container-rg --command "ls -la /data/db /data/images"
```

## API custom domain

The API answers at `api.rss.brandonchastain.com` as well as its Container Apps
hostname, so the browser can share a session cookie between the frontend and the
API. The template binds the hostname (`apiCustomDomain` in `main.bicepparam`)
and declares its free managed certificate, which Azure issues against the DNS
records and renews itself.

DNS lives in the `brchasta` subscription, zone `brandonchastain.com`: a CNAME
`api.rss` to the app's FQDN and a TXT `asuid.api.rss` holding the app's
`customDomainVerificationId`. Both records must exist before a deploy that adds a
new hostname, or certificate issuance fails.

## Read replicas (optional, off)

When `enableReadReplica=true`, a second Container App `rss-reader-api-reader` scales
0 to N and can serve the read-heavy GETs (timeline, feeds, search, content). Nothing
routes traffic to it since the Functions proxy went away (see
[ARCHITECTURE.md](ARCHITECTURE.md#backend-web-api)), so leave it off until a router
exists. Readers restore their database from
the Litestream blob replica at startup, so the writer must run with
`RSSREADER_ENABLE_LITESTREAM_REPLICATION=true`. Enable both in `main.bicepparam` and merge.

```bash
az containerapp show --name rss-reader-api-reader --resource-group rss-container-rg --query properties.runningStatus
az containerapp logs show --name rss-reader-api-reader --resource-group rss-container-rg --type console --tail 20
```

A healthy reader logs `Starting in READER mode (read-only replica with follow-mode restore).`

## Login cutover (Static Web Apps Easy Auth -> API login)

The API can already log users in itself (Google, Microsoft, GitHub; see
[ARCHITECTURE.md](ARCHITECTURE.md#identity-and-roles)) and the frontend can talk to it
directly, but production still runs through SWA Easy Auth and the Functions proxy until
the steps below are done, in order. Nothing before step 6 is visible to users.

**What exists already.** `api.rss.brandonchastain.com` is bound to the API container app
with an Azure managed certificate, both declared in the template (`apiCustomDomain`;
DNS: `CNAME api.rss -> <container app FQDN>` and `TXT asuid.api.rss ->
<customDomainVerificationId>`).

1. **Register the provider apps.** Each needs the production callback
   `https://api.rss.brandonchastain.com/auth/callback/<provider>`; add
   `http://localhost:8080/auth/callback/<provider>` for local testing.
   - Google: Cloud console, OAuth consent screen (external), then an OAuth client of
     type Web application.
   - Microsoft: Entra app registration, account type *any org directory and personal
     Microsoft accounts*, platform Web, plus a client secret.
   - GitHub: Settings > Developer settings > OAuth Apps.
2. **Set the repo secrets** `AUTH_GOOGLE_CLIENT_ID`, `AUTH_GOOGLE_CLIENT_SECRET`,
   `AUTH_MICROSOFT_CLIENT_ID`, `AUTH_MICROSOFT_CLIENT_SECRET`, `AUTH_GITHUB_CLIENT_ID`,
   `AUTH_GITHUB_CLIENT_SECRET` (`gh secret set NAME`). A missing pair disables that
   provider.
3. **Deploy** (merge, or `gh workflow run deploy.yml --ref main`). Check
   `https://api.rss.brandonchastain.com/auth/providers` lists the providers and
   `https://api.rss.brandonchastain.com/auth/me` returns `{"clientPrincipal":null}`.
4. **Link existing Microsoft accounts.** Rows created under Easy Auth carry an SWA user
   id that a direct Microsoft login cannot reproduce, and SWA forwards no other claim.
   While the proxy is still in place the API records a hash of each signed-in user's
   Microsoft-reported email on their row the next time they load the app; the first
   direct Microsoft login after cutover finds the row by that hash and replaces it with
   the real `microsoft:<object id>`. Confirm with your own account: open the site once,
   then `sqlite3 /tmp/storage.db "select Id, AadUserId, ProviderUserId from Users"` via
   `az containerapp exec` should show `microsoft-email:<hash>` next to your SWA id.
   Users who never open the site before step 7 get fresh, empty accounts.
5. **Test the new login against production** before pointing the site at it: visit
   `https://api.rss.brandonchastain.com/auth/login/github?returnUrl=https://rss.brandonchastain.com/`
   in a private window. It must come back to the site with a `.rss.session` cookie set
   for the API host; the site itself still uses Easy Auth at this point, so expect to be
   asked to log in again there.
6. **Point the frontend at the API** in one PR:
   - `src/WasmApp/wwwroot/appsettings.json`: `ApiBaseUrl` = `https://api.rss.brandonchastain.com/`
     (until then the site keeps using Easy Auth even though the API login exists).
   - `src/WasmApp/staticwebapp.config.json`: drop the `/api/healthz` route and the
     `/.auth/*` navigation-fallback exclusion; nothing under `/.auth` is used any more.
   - Add any admin's `microsoft:<guid>` id to `AdminAadUserIds` next to the SWA id.
   - Set the repo variable `gh variable set AUTH_CUTOVER --body true` so the Deploy
     workflow publishes the frontend without the Functions proxy.
   - Merge. Verify: login through each provider, timeline loads, logout returns to the
     site logged out, an existing account sees its feeds.
7. **Retire the proxy** in a final PR once step 6 has held for a few days: delete `api/`,
   the `gatewaySecretKey` parameter, `gateway-secret-key` secret and `RSSREADER_API_KEY`
   env var in `main.bicep`, the `RSSREADER_*` SWA app settings, the `GATEWAY_SECRET_KEY`
   repo secret and the `AUTH_CUTOVER` variable; remove the `EnableStaticWebAppsHeaders`
   flag, the gateway-header handler, `UseStaticWebAppsAuth` with `AuthClient`'s legacy
   branch, and `signedout.html`. Easy Auth itself needs no switching off: with no
   `allowedRoles` routes and no `/.auth` links, SWA never challenges anyone.

**Status:** all seven steps are done. Steps 1 to 6 landed on 2026-10-17 (Microsoft only;
registration "RSS Reader", appId `747a7699-5c86-4614-bd6b-71c44c4012d5`, in the user's
Entra tenant) and the code and template cleanup of step 7 landed on 2026-10-25. The API
no longer accepts the `X-Gateway-Key` / `X-User-Id` headers at all; the only schemes are
the session cookie and, for local development, the fake test user. Leftovers outside the
repo: delete the `GATEWAY_SECRET_KEY` repo secret and `AUTH_CUTOVER` variable, and the
`RSSREADER_*` app settings on the Static Web App (`az staticwebapp appsettings delete`).

**Rollback** of step 7 is a revert of its PR plus restoring the `GATEWAY_SECRET_KEY`
secret; rolling back past step 6 also needs the proxy redeployed, which the template no
longer does.

## Frontend

The Deploy workflow publishes `dotnet publish` output of `src/WasmApp` to Static Web Apps
on every merge to `main`; `wwwroot/appsettings.json` carries the API origin. There is
nothing to run locally; see [LOCAL-TESTING.md](LOCAL-TESTING.md) for the dev loop.
