using './main.bicep'

param location = 'westus2'
param containerAppName = 'rss-reader-api'
param environmentName = 'rss-reader-env'
param logAnalyticsName = 'rss-reader-logs'
param containerImage = 'ghcr.io/brandonchastain/rss-reader-api:latest'
param cpuCore = '1.0'
param memorySize = '2.0Gi'
param minReplicas = 1  // Keep one replica always running to avoid cold starts
param maxReplicas = 1
param ghcrUsername = ''  // specify at deployment time
param ghcrPassword = ''  // specify at deployment time (GitHub PAT with read:packages scope)
param gatewaySecretKey = ''  // specify at deployment time
