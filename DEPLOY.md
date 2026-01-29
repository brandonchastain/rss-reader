# Deploy RSS Reader web app frontend

1. Install prerequisites:
* dotnet
* azure swa cli

2. Run these commands to deploy the frontend:

```bash
cd c:\dev\rssreader\rss-reader\src\WasmApp
dotnet publish -c release -r win-x64 WasmApp.csproj --output bin/release/net9.0/win-x64/publish --self-contained true
swa deploy .\bin\release\net9.0\win-x64\publish\wwwroot\ --env production

```

3. When prompted, choose the WASM app to deploy to.

# Deploy RSS Reader to Azure Container Apps

This guide walks through deploying your RSS Reader API to Azure Container Apps with persistent storage.

## Prerequisites
1. Azure CLI installed: `az --version`
2. Docker installed (for local testing)
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

# NOTE: The container image used below match the containerImage parameter in main.bicepparam
# Example: param containerImage = 'rssreaderacr.azurecr.io/rss-reader-api:latest'
# Deploy the Bicep template
az deployment group create --resource-group rss-container-rg --template-file main.bicep --parameters main.bicepparam --parameters containerImage='rssreaderacr.azurecr.io/rss-reader-api:latest'
```

## Step 4: Configure Container App to Pull from ACR

```bash
# Get ACR credentials
$ACR_USERNAME = (az acr credential show --name rssreaderacr --query username -o tsv)
$ACR_PASSWORD = (az acr credential show --name rssreaderacr --query passwords[0].value -o tsv)

# Update Container App with registry credentials
az containerapp registry set   --name rss-reader-api   --resource-group rss-container-rg   --server rssreaderacr.azurecr.io   --username $ACR_USERNAME   --password $ACR_PASSWORD
```

## Step 5: Get Your API URL

```bash
# Get the FQDN of your Container App
az containerapp show   --name rss-reader-api   --resource-group rss-container-rg   --query properties.configuration.ingress.fqdn   -o tsv
```

This will output something like: `rss-reader-api.kindtree-12345678.westus2.azurecontainerapps.io`

Your API will be available at: `https://rss-reader-api.kindtree-12345678.westus2.azurecontainerapps.io/api/feed`

## Step 6: Update Your Frontend

Update your WasmApp configuration to point to the new Container App URL instead of the VM.

## Monitoring & Logs

```bash
# View container app logs
az containerapp logs show   --name rss-reader-api   --resource-group rss-container-rg   --follow

# Check current replica count (should be 0 when idle)
az containerapp replica list   --name rss-reader-api   --resource-group rss-container-rg
```

## Scale to Zero Behavior

- **When idle**: App scales to 0 replicas, you pay almost nothing
- **On request**: Cold start takes ~3-5 seconds for first request
- **After active**: Stays warm for ~15 minutes before scaling down

## Future Updates

When you update your code:

```bash
# Rebuild and push new image
cd c:\dev\rssreader\rss-reader\src
az acr build --registry rssreaderacr   --image rss-reader-api:latest   --file Server/Dockerfile   .

# Container App automatically pulls latest image on next revision
az containerapp update   --name rss-reader-api   --resource-group rss-container-rg   --image rssreaderacr.azurecr.io/rss-reader-api:latest
```

## Troubleshooting

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
