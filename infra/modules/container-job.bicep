// A manually triggered Container Apps Job: the db-migrator, started by the deploy workflow (design §2).
param name string
param location string
param environmentId string
param identityId string
param registryServer string
param image string
param env array = []

resource job 'Microsoft.App/jobs@2025-01-01' = {
  name: name
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 600
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [
        { server: registryServer, identity: identityId }
      ]
    }
    template: {
      containers: [
        {
          name: 'migrator'
          image: image
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: env
        }
      ]
    }
  }
}
