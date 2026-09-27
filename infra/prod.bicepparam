using './env.bicep'

param environmentName = 'prod'
param imageTag = readEnvironmentVariable('IMAGE_TAG')
param appVersion = readEnvironmentVariable('APP_VERSION', readEnvironmentVariable('IMAGE_TAG'))
param gatewaySigningKey = readEnvironmentVariable('GATEWAY_SIGNING_KEY')
