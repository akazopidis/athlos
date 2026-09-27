// Monthly cost budget for the whole subscription (design §10): alert on actual spend
// reaching the amount, and on the forecast reaching 80% of it.
targetScope = 'subscription'

@description('Monthly budget, in the billing currency of the subscription.')
param amount int = 5

@description('Addresses that receive the budget alerts.')
param contactEmails array

@description('First day of the first budgeted month. Cannot be changed after the budget exists.')
param startDate string = utcNow('yyyy-MM-01')

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: 'budget-athlos'
  properties: {
    category: 'Cost'
    amount: amount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: startDate
    }
    notifications: {
      actual100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: contactEmails
      }
      forecast80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Forecasted'
        contactEmails: contactEmails
      }
    }
  }
}
