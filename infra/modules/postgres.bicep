// The one PostgreSQL server for every stage (design §10, §11): Burstable B1ms, PostgreSQL 17,
// inside the Azure for Students free service. Entra authentication only; the migrator's managed
// identity is the Entra admin and creates each stage's databases and app roles.
param location string
param adminPrincipalId string
param adminPrincipalName string

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'psql-athlos-shared'
  location: location
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '17'
    // 32 GB with auto-grow off stays inside the free service (design §10).
    storage: { storageSizeGB: 32, autoGrow: 'Disabled' }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
      tenantId: subscription().tenantId
    }
    network: { publicNetworkAccess: 'Enabled' }
  }
}

// "Allow public access from Azure services". Private endpoints cost extra (design §11, §14).
resource allowAzureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: server
  name: 'AllowAllAzureServicesAndResourcesWithinAzureIps'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

// The server accepts one change at a time, so the admin waits for the firewall rule.
resource admin 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2024-08-01' = {
  parent: server
  name: adminPrincipalId
  dependsOn: [allowAzureServices]
  properties: {
    principalType: 'ServicePrincipal'
    principalName: adminPrincipalName
    tenantId: subscription().tenantId
  }
}

output fqdn string = server.properties.fullyQualifiedDomainName
