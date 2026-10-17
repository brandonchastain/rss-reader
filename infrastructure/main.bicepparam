using './main.bicep'

param location = 'westus2'
param containerAppName = 'rss-reader-api'
param environmentName = 'rss-reader-env'
param logAnalyticsName = 'rss-reader-logs'
param containerImage = 'ghcr.io/brandonchastain/rss-reader-api:latest'
param cpuCore = '0.5'
param memorySize = '1.0Gi'
// Always keep one replica up. A scale-from-zero cold start measured 45s on
// 2026-10-12 (~20s of platform work before the container even starts), which
// is past the Static Web Apps proxy timeout, so the first request after idle
// returned 500. Idle-replica pricing at 0.5 vCPU / 1 GiB is a few dollars a month.
param minReplicas = 1
param maxReplicas = 1
// The API's own hostname; the template creates its managed certificate.
param apiCustomDomain = 'api.rss.brandonchastain.com'
param gatewaySecretKey = ''  // specify at deployment time
