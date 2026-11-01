using './main.bicep'

param location = 'westus2'
param containerAppName = 'rss-reader-api'
param environmentName = 'rss-reader-env'
param logAnalyticsName = 'rss-reader-logs'
param containerImage = 'ghcr.io/brandonchastain/rss-reader-api:latest'
param cpuCore = '0.25'
param memorySize = '0.5Gi'
// Always keep one replica up. A scale-from-zero cold start measured 45s on
// 2026-10-12 (~20s of platform work before the container even starts), which
// is past the Static Web Apps proxy timeout, so the first request after idle
// returned 500. 0.25 vCPU / 0.5 GiB is the smallest Consumption size: the
// cheapest always-on option. The server is tuned to fit it (RepositoryCache,
// SQLite pragmas, workstation GC).
param minReplicas = 1
param maxReplicas = 1
// The API's own hostname; the template creates its managed certificate.
param apiCustomDomain = 'api.rss.brandonchastain.com'
