# Deploying to production

Production deploys from CI. Merging to `main` runs `.github/workflows/deploy.yml`, which
tests, builds and pushes the backend image, deploys `infrastructure/main.bicep` with that
image tag, and publishes the frontend + API proxy to Static Web Apps. PRs that touch
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
  `AZURE_SUBSCRIPTION_ID` hold the identifiers; the one real secret is `GATEWAY_SECRET_KEY`.
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

## Troubleshooting

**App refuses to boot after a deploy.** The entrypoint seeds `/tmp/storage.db` from
`/data/db/storage.db` and runs `PRAGMA quick_check` on it; a failed check stops the app
rather than let the backup service overwrite the share with bad data. Logs show
`FATAL: PRAGMA quick_check failed`. Restore a known-good copy to the share and redeploy.

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
hostname. DNS (in the `brchasta` subscription, zone `brandonchastain.com`) holds a
CNAME `api.rss` to the app's FQDN and a TXT `asuid.api.rss` with the app's
`customDomainVerificationId`. The template owns the binding through
`apiCustomDomain` and `apiCertificateId`.

Managed certificates cannot be created by the template before the hostname is
bound, so enrolling a new hostname is a one-time manual step:

```bash
az containerapp hostname add  -n rss-reader-api -g rss-container-rg --hostname <host>
az containerapp hostname bind -n rss-reader-api -g rss-container-rg --hostname <host> -e rss-reader-env --validation-method CNAME
az containerapp hostname list -n rss-reader-api -g rss-container-rg --query "[].certificateId" -o tsv
```

Put the certificate id into `main.bicepparam` and merge, so the next deploy keeps
the binding instead of dropping it.

## Read replicas (optional, off)

When `enableReadReplica=true`, a second Container App `rss-reader-api-reader` scales
0 to N and serves the read-heavy GETs (timeline, feeds, search, content) that the proxy
routes to it, falling back to the writer on any error. Readers restore their database from
the Litestream blob replica at startup, so the writer must run with
`RSSREADER_ENABLE_LITESTREAM_REPLICATION=true`. Enable both in `main.bicepparam` and merge.

```bash
az containerapp show --name rss-reader-api-reader --resource-group rss-container-rg --query properties.runningStatus
az containerapp logs show --name rss-reader-api-reader --resource-group rss-container-rg --type console --tail 20
```

A healthy reader logs `Starting in READER mode (read-only replica with follow-mode restore).`

## Frontend

The Deploy workflow publishes the frontend and the Functions API proxy on every merge to
`main`. There is nothing to run locally. `swa-cli.config.json` at the repo root is used
only by `swa start` during local development; see [LOCAL-TESTING.md](LOCAL-TESTING.md).
