# Cheat Sheet

Production deploys from CI. Merging to `main` runs `.github/workflows/deploy.yml`, which
tests, builds and pushes the backend image, deploys `infrastructure/main.bicep` with that
image tag, and publishes the frontend + API proxy to Static Web Apps.

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
  app fails to pull on the next scale-from-zero (`ImagePullBackOff`, 403 from
  `ghcr.io/token`); the fix is to make it public again, not to add a PAT.
- **Azure:** CI logs in with OIDC through the `gh-actions-rss-reader` user-assigned
  managed identity. Repo secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
  `AZURE_SUBSCRIPTION_ID` hold the identifiers; the one real secret is `GATEWAY_SECRET_KEY`.
- **SWA:** the deployment token is read by CI from `az staticwebapp secrets list` at run
  time and never stored.

# Infrastructure buildout and backend deployment

## Prerequisites
1. Azure CLI installed: `az --version`
2. Azure subscription
3. GitHub account with the `gh` CLI authenticated (`gh auth login`)
4. Docker, only if you want to build the image locally for testing

## Step 1: Setup Azure Resources

```bash
# Login to Azure
az login

# Set your subscription
az account set --subscription "YOUR_SUBSCRIPTION_ID"

# Create resource group
az group create --name rss-container-rg --location westus2
```

## Step 1b: Publish the first image

The template references `ghcr.io/<user>/rss-reader-api`, so an image must exist before the
first `az deployment group create`. Push one by running the Deploy workflow once it is
configured (Step 3), or trigger just the build with `gh workflow run deploy.yml --ref main`.

After the first push, open the package on GitHub (Profile > Packages >
`rss-reader-api` > Package settings), set **Visibility** to **Public**, and link it to the
repository. The Container App pulls anonymously and has no registry credential, so a
private package breaks every scale-from-zero.

If you must push a one-off image from a laptop (for example the very first one, before CI
has Azure access), authenticate Docker with the `gh` CLI's token rather than creating a PAT.
The token is piped straight into `docker login` and never printed:

```powershell
gh auth refresh --scopes write:packages
gh auth token | docker login ghcr.io -u brandonchastain --password-stdin
docker build -t ghcr.io/brandonchastain/rss-reader-api:latest -f src/Server/Dockerfile .
docker push ghcr.io/brandonchastain/rss-reader-api:latest
docker logout ghcr.io
```

## Step 3: Infrastructure buildout

> **Everything deploys from CI.** `.github/workflows/deploy.yml` validates the
> template on PRs touching `infrastructure/`, and on merge to `main` runs the
> tests, builds and pushes the backend image, deploys the ARM template with that
> image, then publishes the frontend and API proxy to Static Web Apps. Prefer it
> over running anything below by hand: deploying locally is how the template
> drifted from production in the first place, and how it went unnoticed that ARM
> had stopped accepting the template at all. The steps here remain accurate for
> first-time buildout of a new environment.

```bash
# Navigate to infrastructure directory
cd ..\infrastructure

# Generate a random base64url-encoded secret key
$bytes = New-Object byte[] 64
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$base64 = [Convert]::ToBase64String($bytes)
$GATEWAY_SECRET_KEY = $base64.Replace('+', '-').Replace('/', '_').TrimEnd('=')

# Deploy the Bicep template with the gateway secret key. The image is a public
# GHCR package, so the template takes no registry credential.
az deployment group create `
  --resource-group rss-container-rg `
  --template-file main.bicep `
  --parameters main.bicepparam `
  --parameters containerImage="ghcr.io/brandonchastain/rss-reader-api:latest" `
  --parameters gatewaySecretKey=$GATEWAY_SECRET_KEY

```

Then store `GATEWAY_SECRET_KEY` as a repo secret (`gh secret set GATEWAY_SECRET_KEY`), set
the `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` repo secrets for the
OIDC identity, and let CI own every deploy from here on.

## Future Updates

Merge to `main`. The Deploy workflow builds, pushes, and deploys; see the cheat sheet at
the top of this file to re-run or watch it.

### Monitoring & Logs

```bash
# View container app logs
az containerapp logs show   --name rss-reader-api   --resource-group rss-container-rg   --follow

# Check current replica count (should be 0 when idle)
az containerapp replica list   --name rss-reader-api   --resource-group rss-container-rg

```

### Troubleshooting

### Check container logs
```bash
az containerapp logs show --name rss-reader-api --resource-group rss-container-rg --follow
```

### Verify storage mount
The Azure Files mount at `/data/` persists cached feed images at `/data/images/`. The SQLite database is **not** backed up to Azure Files — it lives at `/tmp/storage.db` and is replicated to Azure Blob Storage by Litestream.

### Litestream Notes
The Docker image includes [Litestream](https://litestream.io/) for continuous SQLite replication to Azure Blob Storage. **Litestream is the sole DB backup path**: the entrypoint runs `litestream restore -if-replica-exists` before starting the app and exits with a fatal error if restore fails (to avoid orphaning the replica with a fresh empty-DB generation). The app then runs under `litestream replicate` supervision, which streams WAL changes to the `litestream` blob container. `DatabaseBackupService` runs alongside but only handles image cache sync and system-stats snapshots — it does not touch the DB file.

On first deployment the blob container is empty, so `litestream restore -if-replica-exists` succeeds silently and the app starts with a brand-new DB; `litestream replicate` then begins seeding the blob.

**Required environment variables** (set via Bicep):
- `LITESTREAM_AZURE_ACCOUNT_NAME` — storage account name
- Authentication uses the Container App's **system-assigned managed identity** (granted `Storage Blob Data Contributor` on the storage account). No account key is needed.

### Check replica status
```bash
az containerapp show --name rss-reader-api --resource-group rss-container-rg --query properties.runningStatus
```

### Read Replicas (optional)

The app supports optional read replicas that scale 0→N to handle read-heavy traffic. Readers restore from the Litestream blob on startup and serve read-only API requests.

**Enable read replicas:**
```bash
az deployment group create \
  --resource-group rss-container-rg \
  --template-file main.bicep \
  --parameters main.bicepparam \
  --parameters enableReadReplica=true \
  --parameters maxReadReplicas=3 \
  --parameters containerImage="ghcr.io/brandonchastain/rss-reader-api:latest" \
  --parameters gatewaySecretKey=$GATEWAY_SECRET_KEY
```

(Prefer setting `enableReadReplica` in `main.bicepparam` and merging, so CI applies it.)

**How it works:**
- The proxy (`ApiProxy.js`) routes GET requests for timeline, feeds, search, and content to the reader
- All writes (mark-as-read, save, add feed, refresh) always go to the writer
- If the reader is down, the proxy automatically falls back to the writer
- Readers are eventually consistent (data is as fresh as their last startup)
- ACA scales readers to zero when idle, each new reader gets a fresh Litestream restore

**Verify reader is running:**
```bash
az containerapp show --name rss-reader-api-reader --resource-group rss-container-rg --query properties.runningStatus
az containerapp logs show --name rss-reader-api-reader --resource-group rss-container-rg --type console --tail 20
```

Healthy reader logs show: `Starting in READER mode (read-only replica).`


# Frontend deployment

The Deploy workflow publishes the frontend and the Functions API proxy on every merge to
`main`, fetching the SWA deployment token from Azure at run time. There is nothing to run
locally. `swa-cli.config.json` at the repo root is still used for `swa start` during local
development.