// One Standard registry for all environments. Standard is the SKU in the Azure for Students
// free services (design §10). Images are pulled with managed identities, so no admin user.
param location string

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: 'acrathlosshared'
  location: location
  sku: { name: 'Standard' }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
  }
}

output loginServer string = registry.properties.loginServer
