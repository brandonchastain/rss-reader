# Cheat Sheet

## Step 1: Build & deploy the backend

Make sure to start Docker first.

```bash
cd c:\dev\rssreader\rss-reader\src
docker build -t ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest -f Server/Dockerfile .
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
# Navigate to the src directory (parent of Server and Shared)
cd c:\dev\rssreader\rss-reader\src

# Build the Docker image
docker build -t ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest -f Server/Dockerfile .

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

### Check replica status
```bash
az containerapp show --name rss-reader-api --resource-group rss-container-rg --query properties.runningStatus
```


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