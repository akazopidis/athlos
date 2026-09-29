// Program media for one stage (design §10: Blob, LRS, a few GB). No account keys, no anonymous
// access: catalog-api writes with its managed identity and hands out 15-minute user-delegation SAS
// links for single blobs (design §3.2, §5.1).
param name string
param location string

@description('Identities that write media and sign read links.')
param contributorPrincipalIds array

// Storage Blob Data Contributor: read, write and delete blobs, and request user-delegation keys.
var blobDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource account 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: name
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2024-01-01' = {
  parent: account
  name: 'default'
}

resource media 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  parent: blobService
  name: 'media'
  properties: { publicAccess: 'None' }
}

resource contributors 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in contributorPrincipalIds: {
  name: guid(account.id, principalId, blobDataContributorRoleId)
  scope: account
  properties: {
    roleDefinitionId: blobDataContributorRoleId
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

output blobEndpoint string = account.properties.primaryEndpoints.blob
