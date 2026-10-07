// The tenant-aware workbook (design §6): per coach request rate, p95 latency, error rate, 429s and
// paid orders, filterable by release. One per environment, on that environment's App Insights.
//
// Requests are sampled (rate-limited sampling keeps a few per second), so request counts sum
// itemCount, the number of requests each kept one stands for. 429s and paid orders come from the
// athlos.ratelimit.rejected and athlos.orders.paid metrics, which are exact.
param location string
param environmentName string
param appInsightsId string

// Coach traffic as the gateway saw it: the spans it tagged with the coach (design §6).
var coachRequests = 'requests | where cloud_RoleName startswith "ca-athlos-gateway" | extend coach = tostring(customDimensions["tenant.slug"]), plan = tostring(customDimensions["tenant.plan"]) | where isnotempty(coach) | where application_Version in ({Version})'

var parameters = {
  type: 9
  name: 'parameters'
  content: {
    version: 'KqlParameterItem/1.0'
    parameters: [
      {
        id: 'time-range'
        version: 'KqlParameterItem/1.0'
        name: 'TimeRange'
        label: 'Time range'
        type: 4
        isRequired: true
        value: { durationMs: 3600000 }
        typeSettings: {
          selectableValues: [
            { durationMs: 900000 }
            { durationMs: 3600000 }
            { durationMs: 14400000 }
            { durationMs: 86400000 }
            { durationMs: 604800000 }
          ]
        }
      }
      {
        id: 'version'
        version: 'KqlParameterItem/1.0'
        name: 'Version'
        label: 'Release'
        type: 2
        multiSelect: true
        quote: '\''
        delimiter: ','
        query: 'requests | where cloud_RoleName startswith "ca-athlos-gateway" | distinct application_Version | order by application_Version desc'
        typeSettings: { additionalResourceOptions: [ 'value::all' ], showDefault: false }
        defaultValue: 'value::all'
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
      }
    ]
  }
}

func chart(name string, title string, query string, visualization string) object => {
  type: 3
  name: name
  content: {
    version: 'KqlItem/1.0'
    title: title
    query: query
    size: 0
    timeContextFromParameter: 'TimeRange'
    queryType: 0
    resourceType: 'microsoft.insights/components'
    visualization: visualization
  }
  customWidth: '50'
}

var items = [
  {
    type: 1
    name: 'intro'
    content: {
      json: '## Athlos per coach (${environmentName})\nCoach traffic through the gateway: studio and storefront requests, tagged with the coach and plan. Request counts are estimates from sampled telemetry; 429s and paid orders are exact metrics.'
    }
  }
  parameters
  chart('rate', 'Requests per minute, per coach', '${coachRequests} | summarize requests = sum(itemCount) by coach, bin(timestamp, 1m) | render timechart', 'timechart')
  chart('p95', 'p95 latency (ms), per coach', '${coachRequests} | summarize p95 = percentile(duration, 95) by coach, bin(timestamp, 1m) | render timechart', 'timechart')
  chart('errors', 'Server errors (5xx), share of requests, per coach', '${coachRequests} | summarize errorRate = round(100.0 * sumif(itemCount, toint(resultCode) >= 500) / sum(itemCount), 2) by coach, bin(timestamp, 1m) | render timechart', 'timechart')
  chart('rejected', 'Refused with 429 (athlos.ratelimit.rejected), per coach', 'customMetrics | where name == "athlos.ratelimit.rejected" | where application_Version in ({Version}) | extend coach = tostring(customDimensions["tenant.slug"]), scope = tostring(customDimensions["scope"]) | summarize refused = sum(valueSum) by who = iff(scope == "tenant", coach, "per client"), bin(timestamp, 1m) | render timechart', 'timechart')
  chart('orders', 'Paid orders (athlos.orders.paid), per coach', 'customMetrics | where name == "athlos.orders.paid" | where application_Version in ({Version}) | summarize paid = sum(valueSum) by coach = tostring(customDimensions["tenant.slug"]), bin(timestamp, 1h) | render columnchart', 'barchart')
  {
    type: 3
    name: 'summary'
    content: {
      version: 'KqlItem/1.0'
      title: 'Per coach, over the time range'
      query: '${coachRequests} | summarize requests = sum(itemCount), p95Ms = round(percentile(duration, 95)), errorsPct = round(100.0 * sumif(itemCount, toint(resultCode) >= 500) / sum(itemCount), 2), refused429 = sumif(itemCount, resultCode == "429") by coach, plan | order by requests desc'
      size: 0
      timeContextFromParameter: 'TimeRange'
      queryType: 0
      resourceType: 'microsoft.insights/components'
      visualization: 'table'
    }
    customWidth: '50'
  }
  {
    type: 3
    name: 'slowest'
    content: {
      version: 'KqlItem/1.0'
      title: 'Slowest coach requests (open one in Transaction search by its operation id)'
      query: '${coachRequests} | top 20 by duration desc | project timestamp, coach, name, resultCode, durationMs = round(duration), operation_Id'
      size: 0
      timeContextFromParameter: 'TimeRange'
      queryType: 0
      resourceType: 'microsoft.insights/components'
      visualization: 'table'
    }
  }
]

resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  // A workbook's name must be a GUID; this one is stable per environment.
  name: guid(resourceGroup().id, 'athlos-per-coach-workbook')
  location: location
  kind: 'shared'
  properties: {
    displayName: 'Athlos per coach (${environmentName})'
    category: 'workbook'
    sourceId: appInsightsId
    serializedData: string({
      version: 'Notebook/1.0'
      items: items
      fallbackResourceIds: [ appInsightsId ]
    })
  }
}

output workbookId string = workbook.id
