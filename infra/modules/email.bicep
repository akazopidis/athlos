// Transactional email (design §5.3): Azure Communication Services Email with an Azure-managed domain
// (DoNotReply@<guid>.azurecomm.net, no DNS setup), one set per stage. Communication Services is a
// global resource that stores its data in a geography; Europe keeps it next to the rest (design §10).
param communicationServiceName string
param emailServiceName string

@description('Identities that send email with their managed identity (no access keys).')
param senderPrincipalIds array = []

var dataLocation = 'Europe'

resource emailService 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: emailServiceName
  location: 'global'
  properties: {
    dataLocation: dataLocation
  }
}

resource domain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: emailService
  name: 'AzureManagedDomain'
  location: 'global'
  properties: {
    domainManagement: 'AzureManaged'
    // Athlos sends receipts and notices, not campaigns; no open or click tracking.
    userEngagementTracking: 'Disabled'
  }
}

resource communicationService 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: communicationServiceName
  location: 'global'
  properties: {
    dataLocation: dataLocation
    linkedDomains: [domain.id]
  }
}

// Sending with Entra ID needs CommunicationServices read/write. The built-in role is broader (it can
// also list keys and delete), so it is scoped to this one resource. A custom role with just the send
// permissions is the hardening step (infra/README.md, known trade-offs): creating role definitions
// needs rights the deploy identity deliberately does not have.
var emailOwnerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '09976791-48a7-449e-bb21-39d1a415f350')

resource senders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in senderPrincipalIds: {
  name: guid(communicationService.id, principalId, emailOwnerRoleId)
  scope: communicationService
  properties: { roleDefinitionId: emailOwnerRoleId, principalId: principalId, principalType: 'ServicePrincipal' }
}]

output endpoint string = 'https://${communicationService.properties.hostName}'
output sender string = 'DoNotReply@${domain.properties.mailFromSenderDomain}'
