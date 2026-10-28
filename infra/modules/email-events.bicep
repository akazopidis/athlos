// Bounces (design §5.3): Communication Services reports each email's delivery to Event Grid; a system
// topic on the stage's Communication Services resource forwards the undeliverable ones (Bounced,
// Suppressed) to the stage's email-events queue, where the notifications-worker records them.
// Event Grid's first 100,000 operations a month are free (design §10); a few hundred emails make a
// few hundred events, and the filter drops the delivered ones before they count as deliveries.
// The namespace allows Entra only (no SAS), so Event Grid delivers with the topic's own identity,
// which may send to this one queue.
param communicationServiceName string
param serviceBusName string
param queueName string

resource communicationService 'Microsoft.Communication/communicationServices@2023-04-01' existing = {
  name: communicationServiceName
}

resource namespace 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: serviceBusName
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' existing = {
  parent: namespace
  name: queueName
}

// Communication Services is a global resource, so its system topic is too (the region policy exempts
// global, design §10).
resource topic 'Microsoft.EventGrid/systemTopics@2025-02-15' = {
  name: '${communicationServiceName}-events'
  location: 'global'
  identity: { type: 'SystemAssigned' }
  properties: {
    source: communicationService.id
    topicType: 'Microsoft.Communication.CommunicationServices'
  }
}

var senderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')

resource topicSends 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(queue.id, topic.id, senderRoleId)
  scope: queue
  properties: { roleDefinitionId: senderRoleId, principalId: topic.identity.principalId, principalType: 'ServicePrincipal' }
}

resource bounces 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2025-02-15' = {
  parent: topic
  name: 'email-bounces'
  dependsOn: [topicSends]
  properties: {
    eventDeliverySchema: 'EventGridSchema'
    filter: {
      includedEventTypes: ['Microsoft.Communication.EmailDeliveryReportReceived']
      advancedFilters: [
        { operatorType: 'StringIn', key: 'data.Status', values: ['Bounced', 'Suppressed'] }
      ]
    }
    deliveryWithResourceIdentity: {
      identity: { type: 'SystemAssigned' }
      destination: {
        endpointType: 'ServiceBusQueue'
        properties: { resourceId: queue.id }
      }
    }
    retryPolicy: { maxDeliveryAttempts: 30, eventTimeToLiveInMinutes: 1440 }
  }
}
