using './env.bicep'

param environmentName = 'prod'
param imageTag = readEnvironmentVariable('IMAGE_TAG')
param appVersion = readEnvironmentVariable('APP_VERSION', readEnvironmentVariable('IMAGE_TAG'))
param gatewaySigningKey = readEnvironmentVariable('GATEWAY_SIGNING_KEY')
param deployApps = bool(readEnvironmentVariable('DEPLOY_APPS', 'true'))
param authAuthority = readEnvironmentVariable('AUTH_AUTHORITY', '')
param authAudience = readEnvironmentVariable('AUTH_AUDIENCE', '')
param spaClientId = readEnvironmentVariable('SPA_CLIENT_ID', '')
param apiScope = readEnvironmentVariable('API_SCOPE', '')
param stripeConfigured = bool(readEnvironmentVariable('STRIPE_CONFIGURED', 'false'))
param alertEmail = readEnvironmentVariable('ALERT_EMAIL', '')
param accountDeletionClientId = readEnvironmentVariable('ACCOUNT_DELETION_CLIENT_ID', '')
