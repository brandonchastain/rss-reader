# Infrastructure and backend deployment

## Prerequisites
1. Azure CLI installed: `az --version`
2. Docker installed
3. Azure subscription

## Step 1: Setup Azure Resources

```bash
# Login to Azure
az login

# Set your subscription
az account set --subscription "YOUR_SUBSCRIPTION_ID"

# Create resource group
az group create --name rss-container-rg --location westus2

# Create Azure Container Registry (ACR) for your images
az acr create --resource-group rss-container-rg   --name rssreaderacr   --sku Basic   --location westus2

# Enable admin access (for easier pushing)
az acr update --name rssreaderacr --admin-enabled true
```

## Step 2: Build and Push Docker Image

```bash
# Navigate to the src directory (parent of Server and Shared)
cd c:\dev\rssreader\rss-reader\src

# Login to ACR
az acr login --name rssreaderacr

# Build and push the image to ACR
az acr build --registry rssreaderacr   --image rss-reader-api:latest   --file Server/Dockerfile   .
```

## Step 3: Deploy Infrastructure

```bash
# Navigate to infrastructure directory
cd ..\infrastructure

# Generate a random base64url-encoded secret key
$bytes = New-Object byte[] 64
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$base64 = [Convert]::ToBase64String($bytes)
$GATEWAY_SECRET_KEY = $base64.Replace('+', '-').Replace('/', '_').TrimEnd('=')

# Deploy the Bicep template with the generated gateway secret key
az deployment group create `
  --resource-group rss-container-rg `
  --template-file main.bicep `
  --parameters main.bicepparam `
  --parameters containerImage='rssreaderacr.azurecr.io/rss-reader-api:latest' `
  --parameters gatewaySecretKey=$GATEWAY_SECRET_KEY

```

## Future Updates

When you update your code:

```bash
# Rebuild and push new image
cd c:\dev\rssreader\rss-reader\src
az acr build --registry rssreaderacr   --image rss-reader-api:latest   --file Server/Dockerfile   .

# Container App automatically pulls latest image on next revision
az containerapp update   --name rss-reader-api   --resource-group rss-container-rg   --image rssreaderacr.azurecr.io/rss-reader-api:latest

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