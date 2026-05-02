using './main.bicep'

param location = 'westus2'
param containerAppName = 'rss-reader-api'
param environmentName = 'rss-reader-env'
param logAnalyticsName = 'rss-reader-logs'
param containerImage = 'ghcr.io/brandonchastain/rss-reader-api:latest'
param cpuCore = '0.5'
param memorySize = '1.0Gi'
param minReplicas = 0  // Scale to zero when idle to minimize cost; cold starts are acceptable for a personal project
param maxReplicas = 1
param ghcrUsername = ''  // specify at deployment time
param ghcrPassword = ''  // specify at deployment time (GitHub PAT with read:packages scope)
param gatewaySecretKey = ''  // specify at deployment time
