// One Key Vault per environment (design §11), RBAC mode. Holds the gateway signing key now;
// the Stripe secrets are added in phase 4.
param name string
param location string

@secure()
@description('Shared by the gateway (signs) and the services (verify). The deploy workflow reuses the stored value.')
param gatewaySigningKey string

@description('Service identities that read secrets.')
param readerPrincipalIds array

@description('The deploying identity, which reads the stored key back on the next deploy.')
param deployerPrincipalId string

var secretsUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: name
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource signingKey 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: vault
  name: 'gateway-signing-key'
  properties: { value: gatewaySigningKey }
}

resource readers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in concat(readerPrincipalIds, [deployerPrincipalId]): {
  name: guid(vault.id, principalId, secretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: secretsUserRoleId
    principalId: principalId
  }
}]

output signingKeyUri string = signingKey.properties.secretUri
