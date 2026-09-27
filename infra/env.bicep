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

@secure()
@description('Gateway signing key (GatewaySignature). The deploy workflow passes the stored value, or a new one on the first deploy.')
param gatewaySigningKey string

param sharedResourceGroup string = 'rg-athlos-shared'
param registryName string = 'acrathlosshared'
param workspaceName string = 'log-athlos-shared'
param acaEnvironmentName string = 'cae-athlos-shared'

var registryServer = '${registryName}.azurecr.io'

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

module tenancyIdentity 'modules/identity.bicep' = {
  name: 'id-tenancy-api'
  params: { name: 'id-athlos-tenancy-api-${environmentName}', location: location }
}

module gatewayIdentity 'modules/identity.bicep' = {
  name: 'id-gateway'
  params: { name: 'id-athlos-gateway-${environmentName}', location: location }
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
    readerPrincipalIds: [tenancyIdentity.outputs.principalId, gatewayIdentity.outputs.principalId]
    deployerPrincipalId: deployer().objectId
  }
}

var signingKeySecret = [{ name: 'gateway-signing-key', keyVaultUrl: keyVault.outputs.signingKeyUri }]

var commonEnv = [
  { name: 'GatewaySignature__Key', secretRef: 'gateway-signing-key' }
  { name: 'APP_VERSION', value: appVersion }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
]

module tenancyApi 'modules/container-app.bicep' = {
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
    keyVaultSecrets: signingKeySecret
    env: concat(commonEnv, [
      { name: 'OTEL_SERVICE_NAME', value: 'tenancy-api' }
    ])
  }
}

module gateway 'modules/container-app.bicep' = {
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
    env: concat(commonEnv, [
      { name: 'OTEL_SERVICE_NAME', value: 'gateway' }
      // Container Apps terminates TLS; trust its X-Forwarded-* headers so HSTS sees https.
      { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
      // Service discovery: the gateway's "https+http://tenancy-api" address resolves to this, as it does under Aspire.
      { name: 'services__tenancy-api__https__0', value: 'https://${tenancyApi.outputs.fqdn}' }
    ])
  }
}

output gatewayUrl string = 'https://${gateway.outputs.fqdn}'
output tenancyApiFqdn string = tenancyApi.outputs.fqdn
