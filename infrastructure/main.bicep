@description('Location for all resources')
param location string = resourceGroup().location

@description('Name of the Container App')
param containerAppName string = 'rss-reader-api'

@description('Name of the Container App Environment')
param environmentName string = 'rss-reader-env'

@description('Name of the Storage Account for persistent data')
param storageAccountName string = take('rssdata${uniqueString(resourceGroup().id)}', 24)

@description('Name of the Log Analytics Workspace')
param logAnalyticsName string = 'rss-reader-logs'

@description('Container image')
param containerImage string

@description('CPU cores allocated to a single container instance')
param cpuCore string = '1.0'

@description('Memory allocated to a single container instance')
param memorySize string = '1.0Gi'

@description('Minimum number of replicas')
param minReplicas int = 0

@description('Maximum number of replicas')
param maxReplicas int = 1

@description('Name of the Static Web App')
param staticWebAppName string = 'rss-reader-swa'

@description('Static Web App SKU')
param staticWebAppSku string = 'Free'

@description('Custom domain for the Static Web App')
param customDomain string = 'rss.brandonchastain.com'

@description('Custom domain for the API Container App. Empty disables the binding.')
param apiCustomDomain string = 'api.rss.brandonchastain.com'


// BLOB-DURABILITY-SPEC.md section 8. Each is a rollout step with its rollback:
// redeploy with the parameter false and the previous behaviour is back.
@description('Rollout step 1: Litestream replication of the live database to blob, beside the file backup')
param enableBlobReplication bool = true

@description('Rollout step 2: blob writer lease taken and logged beside the share lock file')
param enableBlobLease bool = true

@description('Gateway secret key for API authentication')
@secure()
param gatewaySecretKey string

// ---------------------------------------------------------------------------
// Login providers. Each pair lands in the container app's secrets and is
// handed to the API as RssAppConfig__Auth__Providers__<name>__ClientId/Secret.
// Empty means the provider is not offered. The frontend origin the API trusts
// (CORS and returnUrl) is RssAppConfig:Auth:AllowedFrontendOrigins in
// appsettings.json; override it with the env var of the same name if the
// site moves.
// ---------------------------------------------------------------------------
@secure()
param googleClientId string = ''
@secure()
param googleClientSecret string = ''
@secure()
param microsoftClientId string = ''
@secure()
param microsoftClientSecret string = ''
@secure()
param githubClientId string = ''
@secure()
param githubClientSecret string = ''

var loginProviders = [
  { name: 'google', clientId: googleClientId, clientSecret: googleClientSecret }
  { name: 'microsoft', clientId: microsoftClientId, clientSecret: microsoftClientSecret }
  { name: 'github', clientId: githubClientId, clientSecret: githubClientSecret }
]
var configuredLoginProviders = filter(loginProviders, p => !empty(p.clientId) && !empty(p.clientSecret))

// Container app secrets: an entry per configured provider secret. The client
// id is not sensitive but travels the same way so one list drives both.
var loginProviderSecrets = flatten(map(configuredLoginProviders, p => [
  { name: 'auth-${p.name}-client-id', value: p.clientId }
  { name: 'auth-${p.name}-client-secret', value: p.clientSecret }
]))

var loginProviderEnv = flatten(map(configuredLoginProviders, p => [
  { name: 'RssAppConfig__Auth__Providers__${p.name}__ClientId', secretRef: 'auth-${p.name}-client-id' }
  { name: 'RssAppConfig__Auth__Providers__${p.name}__ClientSecret', secretRef: 'auth-${p.name}-client-secret' }
]))

// Log Analytics Workspace
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// Storage Account for persistent SQLite database
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
  }
}

// File Share for SQLite database (legacy, kept for DatabaseBackupService safety net)
resource fileShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = {
  name: '${storageAccount.name}/default/rss-data'
  properties: {
    // A backup cycle briefly holds the old backup AND the new copy on the share
    // (plus the images folder), so the quota must exceed 2x the DB size. At 1 GB
    // a ~530 MB DB crossed that line on 2026-10-02: the copy was truncated and
    // the app refused to boot off it. Azure Files bills used bytes, not quota.
    shareQuota: 5
  }
}

// Blob container for Litestream continuous WAL replication
resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  name: 'default'
  parent: storageAccount
}

// BLOB-DURABILITY-SPEC.md 3.1: the Litestream replica (snapshot + LTX files under
// storage.db/) and, at the container root, the writer-lease blob (3.4).
resource litestreamContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: 'litestream'
  parent: blobServices
  properties: {
    publicAccess: 'None'
  }
}

// Data-protection key ring, used from rollout step 4 (spec 3.6). Created now so
// the switch is a configuration change only.
resource dataProtectionContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: 'dataprotection'
  parent: blobServices
  properties: {
    publicAccess: 'None'
  }
}

// Container App Environment
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// Free managed certificate for the API hostname. Azure validates ownership
// through the api.rss CNAME and renews it automatically. (bindingType 'Auto'
// is rejected by the resource provider at preflight on this API version.)
resource apiCertificate 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' = if (!empty(apiCustomDomain)) {
  name: 'mc-rss-reader-env-api-rss-brandonc-1306'
  parent: environment
  location: location
  properties: {
    subjectName: apiCustomDomain
    domainControlValidation: 'CNAME'
  }
}

// Storage mount for Container App Environment
resource storage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  name: 'rss-data-storage'
  parent: environment
  properties: {
    azureFile: {
      accountName: storageAccount.name
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: 'rss-data'
      accessMode: 'ReadWrite'
    }
  }
}

// Container App
resource containerApp 'Microsoft.App/containerApps@2025-01-01' = {
  name: containerAppName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    environmentId: environment.id
    configuration: {
      // Single-revision mode minimizes old/new revision overlap during deploy.
      // Two writer revisions running concurrently would race on /data/storage.db
      // (mitigated by the BackupToFile lock file, but Single is the proper fix).
      activeRevisionsMode: 'Single'
      // No registries block: the ghcr.io/brandonchastain/rss-reader-api package
      // is public, so ACA pulls anonymously and no registry credential exists to
      // expire, leak, or be forgotten during a rotation.
      secrets: concat([
        {
          name: 'gateway-secret-key'
          value: gatewaySecretKey
        }
      ], loginProviderSecrets)
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
        // The API's own hostname, so the browser can carry a session cookie
        // between the frontend origin and the API.
        customDomains: empty(apiCustomDomain) ? [] : [
          {
            name: apiCustomDomain
            bindingType: 'SniEnabled'
            certificateId: apiCertificate.id
          }
        ]
      }
    }
    template: {
      // Deploy handover (issue #136): on SIGTERM the old revision copies the live
      // SQLite file to the share one last time before it exits, and the new
      // revision waits for that copy before it seeds /tmp. The app's own budget is
      // HostOptions.ShutdownTimeout 110 s (ShutdownBackupTimeout 90 s inside it);
      // this must stay above both or the platform kills the copy mid-write.
      terminationGracePeriodSeconds: 120
      containers: [
        {
          name: 'rss-reader-api'
          image: containerImage
          resources: {
            cpu: json(cpuCore)
            memory: memorySize
          }
          env: concat([
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'RSSREADER_API_KEY'
              secretRef: 'gateway-secret-key'
            }
            {
              name: 'LITESTREAM_AZURE_ACCOUNT_NAME'
              value: storageAccount.name
            }
            {
              // Reduce log volume in production. App still emits Warning+ levels.
              name: 'Logging__LogLevel__Default'
              value: 'Warning'
            }
            {
              // Default off: file-mount backup is sole durability. Set to 'true'
              // only when rolling out scaling mode (read replicas), per the
              // sequenced rollout in plan.md. (Scaling mode is slated for removal,
              // BLOB-DURABILITY-SPEC.md section 4; untouched in rollout steps 1-2.)
              name: 'RSSREADER_ENABLE_LITESTREAM_REPLICATION'
              value: 'false'
            }
            {
              // BLOB-DURABILITY-SPEC.md rollout step 1: Litestream replicates the
              // live database to the `litestream` blob container beside the file
              // backup. The app starts it after the seed. Nothing restores from
              // it at boot yet; the share stays authoritative. Rollback: 'false'.
              name: 'RssAppConfig__Durability__LitestreamEnabled'
              value: string(enableBlobReplication)
            }
            {
              // Rollout step 2: the blob writer lease, taken beside the lock file
              // through the app's managed identity and logged with it. Empty
              // disables the lease (rollback).
              name: 'RssAppConfig__Durability__BlobServiceUri'
              value: enableBlobLease ? storageAccount.properties.primaryEndpoints.blob : ''
            }
            {
              // Persistent SQLite backup file on the Azure Files mount.
              // The DatabaseBackupToFileService writes here every BackupInterval
              // and once more on SIGTERM; the app seeds /tmp/storage.db from this
              // on cold start, after the previous revision's final copy lands.
              //
              // NOTE: Path is intentionally inside a NEW subdirectory `/data/db/`
              // so that any pre-existing `/data/storage.db` (left over from a
              // previous architecture) cannot be silently trusted as a seed.
              name: 'RssAppConfig__BackupDbPath'
              value: '/data/db/storage.db'
            }
            {
              // Session-cookie encryption keys. On the mount so a deploy or a
              // restart does not sign every user out.
              name: 'RssAppConfig__Auth__DataProtectionKeysPath'
              value: '/data/keys'
            }
            {
              // Migration window: keep accepting the Static Web Apps proxy's
              // gateway header. Flip to 'false' in the cutover's final PR.
              name: 'RssAppConfig__Auth__EnableStaticWebAppsHeaders'
              value: 'true'
            }
          ], loginProviderEnv)
          volumeMounts: [
            {
              volumeName: 'rss-data-volume'
              mountPath: '/data'
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/api/healthz'
                port: 8080
              }
              periodSeconds: 30
              timeoutSeconds: 5
              failureThreshold: 3
            }
            {
              type: 'Startup'
              httpGet: {
                path: '/api/healthz'
                port: 8080
              }
              // Probe often: the app finishes booting between probes, and at a 10s
              // period that gap was dead time on every scale-from-zero cold start.
              // failureThreshold is raised to keep the overall startup budget well
              // above the observed ~40s cold start (2s * 150 = 300s).
              periodSeconds: 2
              timeoutSeconds: 5
              failureThreshold: 150
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        // How long KEDA waits before scaling an idle replica away. It must stay
        // comfortably above the app's boot time: during activation a new revision
        // receives no traffic, so a short cooldown kills the replica mid-boot and
        // the revision lands in ActivationFailed holding 100% of the traffic.
        // NOTE: cooldownPeriod is not in the containerApps schema before
        // 2025-01-01. Lowering the resource's apiVersion makes ARM preflight
        // reject the whole template with ContainerAppInvalidSchema.
        cooldownPeriod: 900
        rules: [
          {
            name: 'http-rule'
            http: {
              metadata: {
                concurrentRequests: '10'
              }
            }
          }
        ]
      }
      volumes: [
        {
          name: 'rss-data-volume'
          storageType: 'AzureFile'
          storageName: 'rss-data-storage'
        }
      ]
    }
  }
  dependsOn: [
    storage
  ]
}

// Grant the Container App's managed identity Storage Blob Data Contributor on the storage account
resource blobDataContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, containerApp.id, 'Storage Blob Data Contributor')
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ============================================================================
// READ REPLICA — scales 0→N, read-only, same Docker image
// ============================================================================

@description('Enable read replica Container App')
param enableReadReplica bool = false

@description('Maximum number of read replicas')
param maxReadReplicas int = 3

// Reader Container App — internal ingress, no Azure Files mount
resource readerApp 'Microsoft.App/containerApps@2025-01-01' = if (enableReadReplica) {
  name: '${containerAppName}-reader'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    environmentId: environment.id
    configuration: {
      // No registries block: the ghcr.io/brandonchastain/rss-reader-api package
      // is public, so ACA pulls anonymously and no registry credential exists to
      // expire, leak, or be forgotten during a rotation.
      secrets: [
        {
          name: 'gateway-secret-key'
          value: gatewaySecretKey
        }
      ]
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
    }
    template: {
      containers: [
        {
          name: 'rss-reader-api-reader'
          image: containerImage
          resources: {
            cpu: json(cpuCore)
            memory: memorySize
          }
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'RSSREADER_API_KEY'
              secretRef: 'gateway-secret-key'
            }
            {
              name: 'LITESTREAM_AZURE_ACCOUNT_NAME'
              value: storageAccount.name
            }
            {
              name: 'APP_ROLE'
              value: 'reader'
            }
            {
              name: 'RssAppConfig__IsReadOnly'
              value: 'true'
            }
            {
              name: 'RssAppConfig__DbLocation'
              value: '/tmp/storage.db'
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/api/healthz'
                port: 8080
              }
              periodSeconds: 30
              timeoutSeconds: 5
              failureThreshold: 3
            }
            {
              type: 'Startup'
              httpGet: {
                path: '/api/healthz'
                port: 8080
              }
              // Probe often: the app finishes booting between probes, and at a 10s
              // period that gap was dead time on every scale-from-zero cold start.
              // failureThreshold is raised to keep the overall startup budget well
              // above the observed ~40s cold start (2s * 150 = 300s).
              periodSeconds: 2
              timeoutSeconds: 5
              failureThreshold: 150
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: maxReadReplicas
        cooldownPeriod: 900
        rules: [
          {
            name: 'http-rule'
            http: {
              metadata: {
                concurrentRequests: '10'
              }
            }
          }
        ]
      }
    }
  }
}

// Grant the reader's managed identity Storage Blob Data Reader for Litestream restore
resource readerBlobDataReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableReadReplica) {
  name: guid(storageAccount.id, '${containerAppName}-reader', 'Storage Blob Data Reader')
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
    principalId: readerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Static Web App
resource staticWebApp 'Microsoft.Web/staticSites@2023-01-01' = {
  name: staticWebAppName
  location: location
  sku: {
    name: staticWebAppSku
    tier: staticWebAppSku
  }
  properties: {
    repositoryUrl: ''
    branch: ''
    buildProperties: {
      skipGithubActionWorkflowGeneration: true
    }
  }
}

// Configure Static Web App settings for the managed API
resource staticWebAppSettings 'Microsoft.Web/staticSites/config@2023-01-01' = {
  name: 'appsettings'
  parent: staticWebApp
  properties: {
    RSSREADER_API_URL: 'https://${containerApp.properties.configuration.ingress.fqdn}'
    RSSREADER_API_KEY: gatewaySecretKey
    RSSREADER_READER_API_URL: enableReadReplica ? 'https://${readerApp.properties.configuration.ingress.fqdn}' : ''
  }
}

// Custom Domain for Static Web App
resource staticWebAppCustomDomain 'Microsoft.Web/staticSites/customDomains@2023-01-01' = {
  name: customDomain
  parent: staticWebApp
  properties: {}
}

output containerAppFQDN string = containerApp.properties.configuration.ingress.fqdn
output storageAccountName string = storageAccount.name
output containerAppName string = containerApp.name
output staticWebAppName string = staticWebApp.name
output staticWebAppDefaultHostname string = staticWebApp.properties.defaultHostname
output staticWebAppId string = staticWebApp.id
output readerAppFQDN string = enableReadReplica ? readerApp.properties.configuration.ingress.fqdn : ''
