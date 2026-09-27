// A Container Apps environment in its own VNet, with only the (pay-per-use) Consumption workload
// profile. The environment itself stays public (internal: false) so the gateway can have external
// ingress. Console logs are not forwarded to Log Analytics: OpenTelemetry already sends logs to
// App Insights (design §6).
param name string
param location string
param subnetId string

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: name
  location: location
  properties: {
    appLogsConfiguration: {}
    zoneRedundant: false
    vnetConfiguration: {
      infrastructureSubnetId: subnetId
      internal: false
    }
    workloadProfiles: [
      { name: 'Consumption', workloadProfileType: 'Consumption' }
    ]
  }
}

output id string = environment.id
