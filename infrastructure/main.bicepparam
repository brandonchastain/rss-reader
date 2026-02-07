using './main.bicep'

param location = 'westus2'
param containerAppName = 'rss-reader-api'
param environmentName = 'rss-reader-env'
param logAnalyticsName = 'rss-reader-logs'
param containerImage = 'ghcr.io/brandonchastain/rss-reader-api:latest'
param cpuCore = '0.25'
param memorySize = '0.5Gi'
param minReplicas = 0  // Scale to zero when not in use
param maxReplicas = 1
param ghcrUsername = ''  // specify at deployment time
param ghcrPassword = ''  // specify at deployment time (GitHub PAT with read:packages scope)
param gatewaySecretKey = ''  // specify at deployment time
