// rg-athlos-shared: what every stage uses (design §11). The PostgreSQL server is added with the
// first service that needs a database.
//
// The Container Apps environment is shared too: the student subscription allows only one per
// region. dev, staging and prod apps all run in it, each stage with its own identities, Key Vault,
// signing key and App Insights (design §11, "One environment, three stages").
targetScope = 'resourceGroup'

param location string = resourceGroup().location

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: { location: location }
}

module registry 'modules/registry.bicep' = {
  name: 'registry'
  params: { location: location }
}

module network 'modules/network.bicep' = {
  name: 'network'
  params: {
    name: 'vnet-athlos-shared'
    location: location
    addressPrefix: '10.10.0.0/16'
    acaSubnetPrefix: '10.10.0.0/23'
  }
}

module acaEnvironment 'modules/aca-env.bicep' = {
  name: 'aca-env'
  params: {
    name: 'cae-athlos-shared'
    location: location
    subnetId: network.outputs.acaSubnetId
  }
}

output registryLoginServer string = registry.outputs.loginServer
output workspaceName string = monitoring.outputs.workspaceName
output acaEnvironmentId string = acaEnvironment.outputs.id
