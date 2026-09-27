// rg-athlos-shared: what every environment uses (design §11).
// The PostgreSQL server is added with the first service that needs a database.
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

output registryLoginServer string = registry.outputs.loginServer
output workspaceName string = monitoring.outputs.workspaceName
