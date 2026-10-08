// One service as a container app. Images come from the shared registry via the app's own identity.
param name string
param location string
param environmentId string
param identityId string
param registryServer string
param image string

@description('true only for the gateway; every other app is reachable only inside the environment (design §2).')
param external bool = false

@description('false for a worker without an HTTP API (notifications-worker): no ingress, no HTTP probes, no HTTP scale rule.')
param ingress bool = true

param minReplicas int = 0
param maxReplicas int = 3

@description('vCPU per replica, with memory at the Consumption ratio (2 GiB per vCPU).')
@allowed([ '0.25', '0.5' ])
param cpu string = '0.25'
param env array = []

@description('Key Vault secrets as { name, keyVaultUrl }, read with the app identity; reference them from env with secretRef.')
param keyVaultSecrets array = []

@description('Extra KEDA scale rules next to the HTTP rule, for example a Service Bus queue-length rule (design §4).')
param scaleRules array = []

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: name
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: ingress ? {
        external: external
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      } : null
      registries: [
        { server: registryServer, identity: identityId }
      ]
      secrets: [for secret in keyVaultSecrets: {
        name: secret.name
        keyVaultUrl: secret.keyVaultUrl
        identity: identityId
      }]
    }
    template: {
      containers: [
        {
          name: 'app'
          image: image
          resources: { cpu: json(cpu), memory: cpu == '0.5' ? '1Gi' : '0.5Gi' }
          env: env
          probes: !ingress ? [] : [
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080 }
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              periodSeconds: 10
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: concat(ingress ? [
          { name: 'http', http: { metadata: { concurrentRequests: '50' } } }
        ] : [], scaleRules)
      }
    }
  }
}

output fqdn string = ingress ? app.properties.configuration.ingress.fqdn : ''
