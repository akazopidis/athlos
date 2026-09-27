// One service as a container app. Images come from the shared registry via the app's own identity.
param name string
param location string
param environmentId string
param identityId string
param registryServer string
param image string

@description('true only for the gateway; every other app is reachable only inside the environment (design §2).')
param external bool = false

param minReplicas int = 0
param maxReplicas int = 3
param env array = []

@description('Key Vault secrets as { name, keyVaultUrl }, read with the app identity; reference them from env with secretRef.')
param keyVaultSecrets array = []

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
      ingress: {
        external: external
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
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
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: env
          probes: [
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
        rules: [
          { name: 'http', http: { metadata: { concurrentRequests: '50' } } }
        ]
      }
    }
  }
}

output fqdn string = app.properties.configuration.ingress.fqdn
