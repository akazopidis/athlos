// Prod's alerts (design §6); the other alert, the monthly budget, is budget.bicep. Metric alerts cost
// about €0.10 a month each, and email notifications are free at this volume.
// - Gateway 5xx: the container app's own Requests metric.
// - Dead letters: a message that failed every delivery (an enrollment, a refund, an email) waits in a
//   dead-letter queue, and nothing retries it; a person has to look (design §5.3).
param environmentName string
param gatewayAppId string

@description('The stage\'s Service Bus namespace.')
param serviceBusId string

@description('Who gets the alert email (the ALERT_EMAIL repository variable).')
param email string

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-athlos-${environmentName}'
  location: 'global'
  properties: {
    // At most 12 characters (shown in the email and SMS sender).
    groupShortName: 'athlos-alert'
    enabled: true
    emailReceivers: [
      { name: 'owner', emailAddress: email, useCommonAlertSchema: true }
    ]
  }
}

resource gateway5xx 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-athlos-gateway-5xx-${environmentName}'
  location: 'global'
  properties: {
    description: 'More than 5 server errors (5xx) answered by the ${environmentName} gateway within 5 minutes.'
    severity: 2
    enabled: true
    scopes: [ gatewayAppId ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'gateway-5xx'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'Microsoft.App/containerApps'
          metricName: 'Requests'
          dimensions: [
            { name: 'statusCodeCategory', operator: 'Include', values: [ '5xx' ] }
          ]
          operator: 'GreaterThan'
          threshold: 5
          timeAggregation: 'Total'
        }
      ]
    }
    actions: [
      { actionGroupId: actionGroup.id }
    ]
  }
}

resource deadLetters 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-athlos-dead-letters-${environmentName}'
  location: 'global'
  properties: {
    description: 'A ${environmentName} Service Bus message failed every delivery and is in a dead-letter queue (the EntityName dimension says which queue).'
    severity: 2
    enabled: true
    scopes: [ serviceBusId ]
    evaluationFrequency: 'PT15M'
    windowSize: 'PT15M'
    // Resolves once the dead-letter queues are emptied (handled or resent).
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'dead-letters'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'Microsoft.ServiceBus/namespaces'
          metricName: 'DeadletteredMessages'
          dimensions: [
            { name: 'EntityName', operator: 'Include', values: [ '*' ] }
          ]
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Maximum'
        }
      ]
    }
    actions: [
      { actionGroupId: actionGroup.id }
    ]
  }
}
