// One Log Analytics workspace shared by all environments (design §6, §10).
param location string

@description('Daily ingestion cap in GB. The first 5 GB/month are free; this keeps the month under it.')
param dailyQuotaGb string = '0.15'

resource workspace 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: 'log-athlos-shared'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json(dailyQuotaGb) }
  }
}

output workspaceName string = workspace.name
