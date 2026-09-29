// One Service Bus namespace per stage, Basic tier: queues only, about €0.04 per million operations
// (design §0, §10; topics would need Standard at €8.59/month). Access is Entra-only: no SAS keys.
param name string
param location string

@description('Queue names (kebab-case, design §7).')
param queues array

@description('Identities that send messages (Azure Service Bus Data Sender).')
param senderPrincipalIds array = []

@description('Identities that receive messages (Azure Service Bus Data Receiver).')
param receiverPrincipalIds array = []

var senderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
var receiverRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')

resource namespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: name
  location: location
  sku: { name: 'Basic', tier: 'Basic' }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = [for queueName in queues: {
  parent: namespace
  name: queueName
  properties: {
    // Failed deliveries retry, then land in the dead-letter queue that the 5xx alert also watches (design §5.3).
    maxDeliveryCount: 10
    lockDuration: 'PT1M'
    defaultMessageTimeToLive: 'P14D'
    deadLetteringOnMessageExpiration: true
  }
}]

resource senders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in senderPrincipalIds: {
  name: guid(namespace.id, principalId, senderRoleId)
  scope: namespace
  properties: { roleDefinitionId: senderRoleId, principalId: principalId, principalType: 'ServicePrincipal' }
}]

resource receivers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in receiverPrincipalIds: {
  name: guid(namespace.id, principalId, receiverRoleId)
  scope: namespace
  properties: { roleDefinitionId: receiverRoleId, principalId: principalId, principalType: 'ServicePrincipal' }
}]

output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
