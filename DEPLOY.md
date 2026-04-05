# Cheat Sheet

## Step 1: Build & deploy the backend

Make sure to start Docker first.

```bash
cd c:\dev\rssreader\rss-reader
docker build -t ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest -f src/Server/Dockerfile .
docker push ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest

az containerapp update `
  --name rss-reader-api `
  --resource-group rss-container-rg `
  --image ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest

```


## Step 2: Build & deploy the frontend

```bash
cd c:\dev\rssreader\rss-reader
swa build
swa deploy --env production

```

# Infrastructure buildout and backend deployment

## Prerequisites
1. Azure CLI installed: `az --version`
2. Docker installed
3. Azure subscription
4. Github account (or other container registry)

## Step 1: Setup Azure Resources

```bash
# Login to Azure
az login

# Set your subscription
az account set --subscription "YOUR_SUBSCRIPTION_ID"

# Create resource group
az group create --name rss-container-rg --location westus2
```

## Step 1b: Setup GitHub Container Registry

1. Create a GitHub Personal Access Token (PAT) with `read:packages` and `write:packages` scopes:
   - Go to GitHub Settings > Developer settings > Personal access tokens > Tokens (classic)
   - Generate a new token with `read:packages` and `write:packages` scopes
   - Save the token securely - you'll need it for pushing images and for Azure deployment

2. Login to GitHub Container Registry:
```bash
# Set your GitHub username and PAT
$($env:GITHUB_USERNAME) = "YOUR_GITHUB_USERNAME"
$($env:GITHUB_PAT) = "YOUR_GITHUB_PAT"

# Login to GHCR
echo $($env:GITHUB_PAT) | docker login ghcr.io -u $($env:GITHUB_USERNAME) --password-stdin
```

## Step 2: Build and Push Docker Image

1. Launch Docker.
2. Run these commands:

```bash
# Navigate to the repo root
cd c:\dev\rssreader\rss-reader

# Build the Docker image
docker build -t ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest -f src/Server/Dockerfile .

# Push the image to GitHub Container Registry
docker push ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest
```

## Step 3: Infrastructure buildout

```bash
# Navigate to infrastructure directory
cd ..\infrastructure

# Generate a random base64url-encoded secret key
$bytes = New-Object byte[] 64
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$base64 = [Convert]::ToBase64String($bytes)
$GATEWAY_SECRET_KEY = $base64.Replace('+', '-').Replace('/', '_').TrimEnd('=')

# Deploy the Bicep template with the gateway secret key and GHCR credentials
az deployment group create `
  --resource-group rss-container-rg `
  --template-file main.bicep `
  --parameters main.bicepparam `
  --parameters containerImage="ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest" `
  --parameters gatewaySecretKey=$GATEWAY_SECRET_KEY `
  --parameters ghcrUsername=$($env:GITHUB_USERNAME) `
  --parameters ghcrPassword=$($env:GITHUB_PAT)

```

## Future Updates

When you update your code:

```bash
# Rebuild and push new image
cd c:\dev\rssreader\rss-reader\
docker build -t ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest -f src/Server/Dockerfile .
docker push ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest

az containerapp update `
  --name rss-reader-api `
  --resource-group rss-container-rg `
  --image ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest

```

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
The SQLite database should persist at `/data/storage.db` inside the container, mounted from Azure Files.

### Litestream Migration Notes
The Docker image now includes [Litestream](https://litestream.io/) for continuous SQLite replication to Azure Blob Storage. The entrypoint includes a graceful fallback — if Litestream fails to start (auth error, misconfiguration), the app runs directly with `DatabaseBackupService` providing backup coverage. On first deployment:

1. **Litestream restore is a no-op** — the blob container is empty, so the entrypoint script's `litestream restore -if-replica-exists` succeeds silently.
2. **DatabaseBackupService restores from Azure Files** — the existing backup at `/data/storage.db` is copied to `/tmp/storage.db` as before.
3. **Litestream starts replicating** — WAL changes are continuously streamed to the `litestream` blob container.

On subsequent boots, Litestream restores from blob (more up-to-date than Azure Files), and `DatabaseBackupService` skips its own DB restore because the active DB already exists, but still restores cached images from Azure Files.

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
  --parameters containerImage="ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest" \
  --parameters gatewaySecretKey=$GATEWAY_SECRET_KEY \
  --parameters ghcrUsername=$($env:GITHUB_USERNAME) \
  --parameters ghcrPassword=$($env:GITHUB_PAT)
```

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

## Step 1: Install prerequisites

* dotnet
* Node.js and npm
* azure swa cli

## Step 2: Build & deploy the frontend

Before running, double-check that swa-cli.config.json to points to your SWA.

```bash
cd c:\dev\rssreader\rss-reader
swa build
swa deploy --env production

```