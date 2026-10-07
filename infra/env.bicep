// One stage (dev, staging or prod), deployed into rg-athlos-{environmentName} (design §11).
// Its apps run in the shared Container Apps environment in rg-athlos-shared.
targetScope = 'resourceGroup'

@allowed(['dev', 'staging', 'prod'])
param environmentName string

param location string = resourceGroup().location

@description('Image tag to run, for example sha-3f2a1c9 or v0.1.0. Images are built once and promoted (design §12.2).')
param imageTag string

@description('Release shown on /version and as the OpenTelemetry service.version.')
param appVersion string = imageTag

@description('Set to 1 on prod during demos to avoid a cold start; costs about €5/month (design §10).')
param gatewayMinReplicas int = 0

@description('false on the first pass of a deploy: everything except the apps, so the migrator job can prepare the database before the apps start.')
param deployApps bool = true

@description('Entra External ID authority, for example https://athlos.ciamlogin.com/<tenant id>/v2.0. Empty until Entra is set up.')
param authAuthority string = ''

@description('Client id of the Athlos API app registration. Empty until Entra is set up.')
param authAudience string = ''

@description('Client id of the SPA app registration, served to the browser by /config.json.')
param spaClientId string = ''

@description('The API scope the SPA asks for, for example api://<api client id>/access_as_user.')
param apiScope string = ''

@description('true once the stage Key Vault holds stripe-secret-key (infra/stripe_setup.py). Referencing a missing Key Vault secret would fail the deploy.')
param stripeConfigured bool = false

@description('true in dev and staging: the migrator adds the demo coaches, programs and purchases (design §15.2). Never in prod.')
param seedDemoData bool = false

@secure()
@description('Gateway signing key (GatewaySignature). The deploy workflow passes the stored value, or a new one on the first deploy.')
param gatewaySigningKey string

param sharedResourceGroup string = 'rg-athlos-shared'
param registryName string = 'acrathlosshared'
param workspaceName string = 'log-athlos-shared'
param acaEnvironmentName string = 'cae-athlos-shared'
param postgresServerName string = 'psql-athlos-shared'
param migratorIdentityName string = 'id-athlos-migrator-shared'

var registryServer = '${registryName}.azurecr.io'

// Database per service, per stage; each service connects as its own non-owner role (design §2.1, §7).
var tenancyDatabase = 'athlos_tenancy_${environmentName}'
var tenancyAppRole = 'athlos_tenancy_${environmentName}_app'
var catalogDatabase = 'athlos_catalog_${environmentName}'
var catalogAppRole = 'athlos_catalog_${environmentName}_app'
var orderingDatabase = 'athlos_ordering_${environmentName}'
var orderingAppRole = 'athlos_ordering_${environmentName}_app'
var enrollmentDatabase = 'athlos_enrollment_${environmentName}'
var enrollmentAppRole = 'athlos_enrollment_${environmentName}_app'

resource workspace 'Microsoft.OperationalInsights/workspaces@2025-02-01' existing = {
  name: workspaceName
  scope: resourceGroup(sharedResourceGroup)
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-athlos-${environmentName}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

resource acaEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: acaEnvironmentName
  scope: resourceGroup(sharedResourceGroup)
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' existing = {
  name: postgresServerName
  scope: resourceGroup(sharedResourceGroup)
}

resource migratorIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: migratorIdentityName
  scope: resourceGroup(sharedResourceGroup)
}

var postgresHost = postgres.properties.fullyQualifiedDomainName

module tenancyIdentity 'modules/identity.bicep' = {
  name: 'id-tenancy-api'
  params: { name: 'id-athlos-tenancy-api-${environmentName}', location: location }
}

module gatewayIdentity 'modules/identity.bicep' = {
  name: 'id-gateway'
  params: { name: 'id-athlos-gateway-${environmentName}', location: location }
}

module catalogIdentity 'modules/identity.bicep' = {
  name: 'id-catalog-api'
  params: { name: 'id-athlos-catalog-api-${environmentName}', location: location }
}

module catalogAcrPull 'modules/acr-pull.bicep' = {
  name: 'acr-pull-catalog-api-${environmentName}'
  scope: resourceGroup(sharedResourceGroup)
  params: { registryName: registryName, principalId: catalogIdentity.outputs.principalId }
}

module orderingIdentity 'modules/identity.bicep' = {
  name: 'id-ordering-api'
  params: { name: 'id-athlos-ordering-api-${environmentName}', location: location }
}

module orderingAcrPull 'modules/acr-pull.bicep' = {
  name: 'acr-pull-ordering-api-${environmentName}'
  scope: resourceGroup(sharedResourceGroup)
  params: { registryName: registryName, principalId: orderingIdentity.outputs.principalId }
}

module enrollmentIdentity 'modules/identity.bicep' = {
  name: 'id-enrollment-api'
  params: { name: 'id-athlos-enrollment-api-${environmentName}', location: location }
}

module enrollmentAcrPull 'modules/acr-pull.bicep' = {
  name: 'acr-pull-enrollment-api-${environmentName}'
  scope: resourceGroup(sharedResourceGroup)
  params: { registryName: registryName, principalId: enrollmentIdentity.outputs.principalId }
}

module notificationsIdentity 'modules/identity.bicep' = {
  name: 'id-notifications-worker'
  params: { name: 'id-athlos-notifications-worker-${environmentName}', location: location }
}

module notificationsAcrPull 'modules/acr-pull.bicep' = {
  name: 'acr-pull-notifications-worker-${environmentName}'
  scope: resourceGroup(sharedResourceGroup)
  params: { registryName: registryName, principalId: notificationsIdentity.outputs.principalId }
}

var serviceBusName = 'sb-athlos-${environmentName}'

// Events between services (design §2.2): ordering sends OrderPaid, enrollment receives it; ordering,
// enrollment and tenancy queue emails, the notifications-worker receives them (design §5.3).
// Roles are on the namespace: every identity here is one of this stage's own services.
module serviceBus 'modules/servicebus.bicep' = {
  name: 'servicebus'
  params: {
    name: serviceBusName
    location: location
    queues: ['order-paid', 'notifications']
    senderPrincipalIds: [orderingIdentity.outputs.principalId, enrollmentIdentity.outputs.principalId, tenancyIdentity.outputs.principalId]
    receiverPrincipalIds: [enrollmentIdentity.outputs.principalId, notificationsIdentity.outputs.principalId]
  }
}

// Communication Services Email, Azure-managed domain (design §5.3, §10).
module email 'modules/email.bicep' = {
  name: 'email'
  params: {
    communicationServiceName: 'acs-athlos-${environmentName}'
    emailServiceName: 'ecs-athlos-${environmentName}'
    senderPrincipalIds: [notificationsIdentity.outputs.principalId]
  }
}

module tenancyAcrPull 'modules/acr-pull.bicep' = {
  name: 'acr-pull-tenancy-api-${environmentName}'
  scope: resourceGroup(sharedResourceGroup)
  params: { registryName: registryName, principalId: tenancyIdentity.outputs.principalId }
}

module gatewayAcrPull 'modules/acr-pull.bicep' = {
  name: 'acr-pull-gateway-${environmentName}'
  scope: resourceGroup(sharedResourceGroup)
  params: { registryName: registryName, principalId: gatewayIdentity.outputs.principalId }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: 'kv-athlos-${environmentName}'
    location: location
    gatewaySigningKey: gatewaySigningKey
    readerPrincipalIds: [tenancyIdentity.outputs.principalId, catalogIdentity.outputs.principalId, orderingIdentity.outputs.principalId, enrollmentIdentity.outputs.principalId, notificationsIdentity.outputs.principalId, gatewayIdentity.outputs.principalId]
    deployerPrincipalId: deployer().objectId
  }
}

var signingKeySecret = [{ name: 'gateway-signing-key', keyVaultUrl: keyVault.outputs.signingKeyUri }]

// Stripe secrets are written into Key Vault by infra/stripe_setup.py, not by Bicep (no secret in the repo or pipeline).
var stripeSecretKey = [{ name: 'stripe-secret-key', keyVaultUrl: '${keyVault.outputs.vaultUri}secrets/stripe-secret-key' }]
var stripeWebhookSecret = [{ name: 'stripe-webhook-secret', keyVaultUrl: '${keyVault.outputs.vaultUri}secrets/stripe-webhook-secret' }]

// The stage's public address: its gateway (container app names are fixed, the domain is the environment's).
var publicUrl = 'https://ca-athlos-gateway-${environmentName}.${acaEnvironment.properties.defaultDomain}'

var commonEnv = [
  { name: 'GatewaySignature__Key', secretRef: 'gateway-signing-key' }
  { name: 'APP_VERSION', value: appVersion }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
]

// Runs before the apps on every deploy (see deployApps), started by _deploy.yml.
module migrator 'modules/container-job.bicep' = {
  name: 'caj-migrator'
  params: {
    name: 'caj-athlos-migrator-${environmentName}'
    location: location
    environmentId: acaEnvironment.id
    identityId: migratorIdentity.id
    registryServer: registryServer
    image: '${registryServer}/athlos/db-migrator:${imageTag}'
    env: [
      { name: 'ConnectionStrings__tenancy-db-owner', value: 'Host=${postgresHost};Database=${tenancyDatabase};Username=${migratorIdentity.name};SSL Mode=Require' }
      { name: 'Databases__Tenancy__AppRole', value: tenancyAppRole }
      { name: 'Databases__Tenancy__AppPrincipalObjectId', value: tenancyIdentity.outputs.principalId }
      { name: 'ConnectionStrings__catalog-db-owner', value: 'Host=${postgresHost};Database=${catalogDatabase};Username=${migratorIdentity.name};SSL Mode=Require' }
      { name: 'Databases__Catalog__AppRole', value: catalogAppRole }
      { name: 'Databases__Catalog__AppPrincipalObjectId', value: catalogIdentity.outputs.principalId }
      { name: 'ConnectionStrings__ordering-db-owner', value: 'Host=${postgresHost};Database=${orderingDatabase};Username=${migratorIdentity.name};SSL Mode=Require' }
      { name: 'Databases__Ordering__AppRole', value: orderingAppRole }
      { name: 'Databases__Ordering__AppPrincipalObjectId', value: orderingIdentity.outputs.principalId }
      { name: 'ConnectionStrings__enrollment-db-owner', value: 'Host=${postgresHost};Database=${enrollmentDatabase};Username=${migratorIdentity.name};SSL Mode=Require' }
      { name: 'Databases__Enrollment__AppRole', value: enrollmentAppRole }
      { name: 'Databases__Enrollment__AppPrincipalObjectId', value: enrollmentIdentity.outputs.principalId }
      { name: 'AZURE_CLIENT_ID', value: migratorIdentity.properties.clientId }
      { name: 'Seed__DemoData', value: string(seedDemoData) }
      { name: 'APP_VERSION', value: appVersion }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      { name: 'OTEL_SERVICE_NAME', value: 'db-migrator' }
    ]
  }
}

var authEnv = [
  { name: 'Auth__Authority', value: authAuthority }
  { name: 'Auth__Audience', value: authAudience }
]

module tenancyApi 'modules/container-app.bicep' = if (deployApps) {
  name: 'ca-tenancy-api'
  dependsOn: [tenancyAcrPull]
  params: {
    name: 'ca-athlos-tenancy-api-${environmentName}'
    location: location
    environmentId: acaEnvironment.id
    identityId: tenancyIdentity.outputs.id
    registryServer: registryServer
    image: '${registryServer}/athlos/tenancy-api:${imageTag}'
    maxReplicas: 3
    keyVaultSecrets: concat(signingKeySecret, stripeConfigured ? stripeSecretKey : [])
    env: concat(commonEnv, authEnv, [
      { name: 'OTEL_SERVICE_NAME', value: 'tenancy-api' }
      { name: 'App__PublicUrl', value: publicUrl }
      { name: 'AZURE_CLIENT_ID', value: tenancyIdentity.outputs.clientId }
      // No password: the app signs in with its managed identity's token (PostgresEntraAuthentication).
      { name: 'ConnectionStrings__tenancy-db', value: 'Host=${postgresHost};Database=${tenancyDatabase};Username=${tenancyAppRole};SSL Mode=Require;Maximum Pool Size=3;Options=-c statement_timeout=5000' }
      // Staff invite emails leave through the outbox (design §5.2); a namespace, no key.
      { name: 'ConnectionStrings__messaging', value: serviceBus.outputs.fullyQualifiedNamespace }
      // Deleting a workspace calls catalog and enrollment (design §3.4). Their internal addresses are
      // built from the environment's domain, not module outputs: those apps already depend on tenancy.
      { name: 'services__catalog-api__https__0', value: 'https://ca-athlos-catalog-api-${environmentName}.internal.${acaEnvironment.properties.defaultDomain}' }
      { name: 'services__enrollment-api__https__0', value: 'https://ca-athlos-enrollment-api-${environmentName}.internal.${acaEnvironment.properties.defaultDomain}' }
    ], stripeConfigured ? [{ name: 'Stripe__SecretKey', secretRef: 'stripe-secret-key' }] : [])
  }
}

// Names that forbid dashes have none (design §7): stathlosdev, stathlosstaging, stathlosprod.
module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    name: 'stathlos${environmentName}'
    location: location
    contributorPrincipalIds: [catalogIdentity.outputs.principalId]
  }
}

module catalogApi 'modules/container-app.bicep' = if (deployApps) {
  name: 'ca-catalog-api'
  dependsOn: [catalogAcrPull]
  params: {
    name: 'ca-athlos-catalog-api-${environmentName}'
    location: location
    environmentId: acaEnvironment.id
    identityId: catalogIdentity.outputs.id
    registryServer: registryServer
    image: '${registryServer}/athlos/catalog-api:${imageTag}'
    maxReplicas: 3
    keyVaultSecrets: signingKeySecret
    env: concat(commonEnv, authEnv, [
      { name: 'OTEL_SERVICE_NAME', value: 'catalog-api' }
      { name: 'AZURE_CLIENT_ID', value: catalogIdentity.outputs.clientId }
      // A blob endpoint, no key: the client signs in with AZURE_CLIENT_ID's managed identity.
      { name: 'ConnectionStrings__media', value: storage.outputs.blobEndpoint }
      { name: 'services__tenancy-api__https__0', value: 'https://${tenancyApi!.outputs.fqdn}' }
      { name: 'ConnectionStrings__catalog-db', value: 'Host=${postgresHost};Database=${catalogDatabase};Username=${catalogAppRole};SSL Mode=Require;Maximum Pool Size=3;Options=-c statement_timeout=5000' }
    ])
  }
}

module orderingApi 'modules/container-app.bicep' = if (deployApps) {
  name: 'ca-ordering-api'
  dependsOn: [orderingAcrPull]
  params: {
    name: 'ca-athlos-ordering-api-${environmentName}'
    location: location
    environmentId: acaEnvironment.id
    identityId: orderingIdentity.outputs.id
    registryServer: registryServer
    image: '${registryServer}/athlos/ordering-api:${imageTag}'
    maxReplicas: 3
    keyVaultSecrets: concat(signingKeySecret, stripeConfigured ? concat(stripeSecretKey, stripeWebhookSecret) : [])
    env: concat(commonEnv, authEnv, [
      { name: 'OTEL_SERVICE_NAME', value: 'ordering-api' }
      { name: 'AZURE_CLIENT_ID', value: orderingIdentity.outputs.clientId }
      { name: 'App__PublicUrl', value: publicUrl }
      { name: 'ConnectionStrings__ordering-db', value: 'Host=${postgresHost};Database=${orderingDatabase};Username=${orderingAppRole};SSL Mode=Require;Maximum Pool Size=3;Options=-c statement_timeout=5000' }
      // A namespace, no key: the client signs in with AZURE_CLIENT_ID's managed identity.
      { name: 'ConnectionStrings__messaging', value: serviceBus.outputs.fullyQualifiedNamespace }
      { name: 'services__catalog-api__https__0', value: 'https://${catalogApi!.outputs.fqdn}' }
      { name: 'services__tenancy-api__https__0', value: 'https://${tenancyApi!.outputs.fqdn}' }
    ], stripeConfigured ? [
      { name: 'Stripe__SecretKey', secretRef: 'stripe-secret-key' }
      { name: 'Stripe__WebhookSecret', secretRef: 'stripe-webhook-secret' }
    ] : [])
  }
}

module enrollmentApi 'modules/container-app.bicep' = if (deployApps) {
  name: 'ca-enrollment-api'
  dependsOn: [enrollmentAcrPull]
  params: {
    name: 'ca-athlos-enrollment-api-${environmentName}'
    location: location
    environmentId: acaEnvironment.id
    identityId: enrollmentIdentity.outputs.id
    registryServer: registryServer
    image: '${registryServer}/athlos/enrollment-api:${imageTag}'
    maxReplicas: 3
    keyVaultSecrets: signingKeySecret
    // Scales up from zero when OrderPaid messages wait, not only on HTTP traffic (design §4, row 4).
    // KEDA reads the queue length with the app's own identity (Data Receiver on the namespace).
    scaleRules: [
      {
        name: 'order-paid-queue'
        custom: {
          type: 'azure-servicebus'
          identity: enrollmentIdentity.outputs.id
          metadata: {
            namespace: serviceBusName
            queueName: 'order-paid'
            messageCount: '5'
          }
        }
      }
    ]
    env: concat(commonEnv, authEnv, [
      { name: 'OTEL_SERVICE_NAME', value: 'enrollment-api' }
      { name: 'AZURE_CLIENT_ID', value: enrollmentIdentity.outputs.clientId }
      { name: 'App__PublicUrl', value: publicUrl }
      { name: 'ConnectionStrings__enrollment-db', value: 'Host=${postgresHost};Database=${enrollmentDatabase};Username=${enrollmentAppRole};SSL Mode=Require;Maximum Pool Size=3;Options=-c statement_timeout=5000' }
      // A namespace, no key: the client signs in with AZURE_CLIENT_ID's managed identity.
      { name: 'ConnectionStrings__messaging', value: serviceBus.outputs.fullyQualifiedNamespace }
      { name: 'services__catalog-api__https__0', value: 'https://${catalogApi!.outputs.fqdn}' }
    ])
  }
}

// No HTTP API (design §2.1): no ingress, scales from zero on the notifications queue only.
module notificationsWorker 'modules/container-app.bicep' = if (deployApps) {
  name: 'ca-notifications-worker'
  dependsOn: [notificationsAcrPull]
  params: {
    // Container app names are at most 32 characters: 'ca-athlos-notifications-worker-staging' would be 38.
    name: 'ca-athlos-notifications-${environmentName}'
    location: location
    environmentId: acaEnvironment.id
    identityId: notificationsIdentity.outputs.id
    registryServer: registryServer
    image: '${registryServer}/athlos/notifications-worker:${imageTag}'
    ingress: false
    maxReplicas: 2
    keyVaultSecrets: signingKeySecret
    scaleRules: [
      {
        name: 'notifications-queue'
        custom: {
          type: 'azure-servicebus'
          identity: notificationsIdentity.outputs.id
          metadata: {
            namespace: serviceBusName
            queueName: 'notifications'
            messageCount: '10'
          }
        }
      }
    ]
    env: concat(commonEnv, [
      { name: 'OTEL_SERVICE_NAME', value: 'notifications-worker' }
      { name: 'AZURE_CLIENT_ID', value: notificationsIdentity.outputs.clientId }
      { name: 'ConnectionStrings__messaging', value: serviceBus.outputs.fullyQualifiedNamespace }
      { name: 'services__tenancy-api__https__0', value: 'https://${tenancyApi!.outputs.fqdn}' }
      { name: 'Email__Mode', value: 'Acs' }
      { name: 'Email__Endpoint', value: email.outputs.endpoint }
      { name: 'Email__Sender', value: email.outputs.sender }
    ])
  }
}

module gateway 'modules/container-app.bicep' = if (deployApps) {
  name: 'ca-gateway'
  dependsOn: [gatewayAcrPull]
  params: {
    name: 'ca-athlos-gateway-${environmentName}'
    location: location
    environmentId: acaEnvironment.id
    identityId: gatewayIdentity.outputs.id
    registryServer: registryServer
    image: '${registryServer}/athlos/gateway:${imageTag}'
    external: true
    minReplicas: gatewayMinReplicas
    // The in-memory rate limiter multiplies with replicas, so the gateway stays at 2 (design §4).
    maxReplicas: 2
    keyVaultSecrets: signingKeySecret
    env: concat(commonEnv, authEnv, [
      { name: 'OTEL_SERVICE_NAME', value: 'gateway' }
      // Container Apps terminates TLS; trust its X-Forwarded-* headers so HSTS sees https.
      { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
      // SPA sign-in settings, served by GET /config.json (design §8.1).
      { name: 'Spa__EntraClientId', value: spaClientId }
      { name: 'Spa__EntraAuthority', value: authAuthority }
      { name: 'Spa__ApiScope', value: apiScope }
      // Service discovery: the gateway's "https+http://tenancy-api" address resolves to this, as it does under Aspire.
      { name: 'services__tenancy-api__https__0', value: 'https://${tenancyApi!.outputs.fqdn}' }
      { name: 'services__catalog-api__https__0', value: 'https://${catalogApi!.outputs.fqdn}' }
      { name: 'services__ordering-api__https__0', value: 'https://${orderingApi!.outputs.fqdn}' }
      { name: 'services__enrollment-api__https__0', value: 'https://${enrollmentApi!.outputs.fqdn}' }
    ])
  }
}

output gatewayUrl string = deployApps ? 'https://${gateway!.outputs.fqdn}' : ''
output tenancyApiFqdn string = deployApps ? tenancyApi!.outputs.fqdn : ''
output catalogApiFqdn string = deployApps ? catalogApi!.outputs.fqdn : ''
output orderingApiFqdn string = deployApps ? orderingApi!.outputs.fqdn : ''
output enrollmentApiFqdn string = deployApps ? enrollmentApi!.outputs.fqdn : ''
output migratorJobName string = 'caj-athlos-migrator-${environmentName}'
