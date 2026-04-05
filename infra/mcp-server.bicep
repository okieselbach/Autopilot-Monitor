// Autopilot-Monitor MCP Server — Azure Container App Infrastructure
//
// Deploy:
//   az deployment group create \
//     --resource-group <rg-name> \
//     --template-file infra/mcp-server.bicep \
//     --parameters apiUrl=https://autopilotmonitor-api.azurewebsites.net \
//                  entraClientSecret=<secret-value>

@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Name of the Container App')
param containerAppName string = 'autopilotmonitor-mcp'

@description('Name of the Container App Environment')
param environmentName string = 'autopilotmonitor-env'

@description('Name of the Log Analytics Workspace (dedicated for Container Apps)')
param logAnalyticsName string = 'autopilotmonitor-container-logs'

@description('Name of the Azure Container Registry')
param acrName string = 'autopilotmonitoracr'

@description('Backend API URL')
param apiUrl string = 'https://autopilotmonitor-api.azurewebsites.net'

@description('Container image tag')
param imageTag string = 'latest'

@secure()
@description('Entra ID client secret for OAuth token exchange')
param entraClientSecret string = ''

// --- Azure Container Registry ---

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
}

// --- Log Analytics Workspace (required by Container App Environment) ---

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

// --- Container App Environment ---

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

// --- Container App ---

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
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
        {
          name: 'entra-client-secret'
          value: entraClientSecret
        }
      ]
      activeRevisionsMode: 'Single'
    }
    template: {
      containers: [
        {
          name: 'mcp-server'
          image: '${acr.properties.loginServer}/${containerAppName}:${imageTag}'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'AUTOPILOT_API_URL', value: apiUrl }
            { name: 'PORT', value: '8080' }
            { name: 'AUTOPILOT_ENTRA_CLIENT_SECRET', secretRef: 'entra-client-secret' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              periodSeconds: 30
              initialDelaySeconds: 10
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              periodSeconds: 10
              initialDelaySeconds: 5
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [
          {
            name: 'http-scaler'
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

// --- Outputs ---

output containerAppUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
output mcpEndpoint string = 'https://${containerApp.properties.configuration.ingress.fqdn}/mcp'
output acrLoginServer string = acr.properties.loginServer
