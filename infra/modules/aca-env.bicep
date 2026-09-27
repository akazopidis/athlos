// A Consumption-only Container Apps environment. Console logs are not forwarded to
// Log Analytics: OpenTelemetry already sends logs to App Insights (design §6).
param name string
param location string

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: name
  location: location
  properties: {
    appLogsConfiguration: {}
    zoneRedundant: false
  }
}

output id string = environment.id
