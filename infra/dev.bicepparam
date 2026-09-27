using './env.bicep'

param environmentName = 'dev'
param imageTag = readEnvironmentVariable('IMAGE_TAG')
param appVersion = readEnvironmentVariable('APP_VERSION', readEnvironmentVariable('IMAGE_TAG'))
