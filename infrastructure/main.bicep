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
param cpuCore string = '0.25'

@description('Memory allocated to a single container instance')
param memorySize string = '0.5Gi'

@description('Minimum number of replicas')
param minReplicas int = 0

@description('Maximum number of replicas')
param maxReplicas int = 1

@description('Azure Container Registry name')
param acrName string = 'rssreaderacr'

@description('Name of the Static Web App')
param staticWebAppName string = 'rss-reader-swa'

@description('Static Web App SKU')
param staticWebAppSku string = 'Free'

@description('Custom domain for the Static Web App')
param customDomain string = 'rss.brandonchastain.com'

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

// File Share for SQLite database
resource fileShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = {
  name: '${storageAccount.name}/default/rss-data'
  properties: {
    shareQuota: 1  // 1 GB should be plenty for SQLite
  }
}

// Reference existing ACR
resource acr 'Microsoft.ContainerRegistry/registries@2023-01-01-preview' existing = {
  name: acrName
  scope: resourceGroup()
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
resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  properties: {
    environmentId: environment.id
    configuration: {
      registries: [
        {
          server: acr.properties.loginServer
          username: acr.listCredentials().username
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: [
        {
          name: 'acr-password'
          value: acr.listCredentials().passwords[0].value
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
          name: 'rss-reader-api'
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
          ]
          volumeMounts: [
            {
              volumeName: 'rss-data-volume'
              mountPath: '/data'
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
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
