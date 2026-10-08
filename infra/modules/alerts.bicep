// The gateway 5xx alert (design §6), prod only; the other alert, the monthly budget, is budget.bicep.
// A metric alert on the container app's own Requests metric: about €0.10 a month, and email
// notifications are free at this volume.
param environmentName string
param gatewayAppId string

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
