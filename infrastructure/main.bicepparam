using './main.bicep'

param location = 'westus2'
param containerAppName = 'rss-reader-api'
param environmentName = 'rss-reader-env'
param logAnalyticsName = 'rss-reader-logs'
param containerImage = 'rssreaderacr.azurecr.io/rss-reader-api:latest'
param cpuCore = '0.25'
param memorySize = '0.5Gi'
param minReplicas = 0  // Scale to zero when not in use
param maxReplicas = 1
