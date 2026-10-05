// Noisy-neighbor experiment (design §4), run against staging with the demo coaches (design §15.2).
//
//   k6 run -e BASE_URL=https://<staging gateway> -e MODE=baseline -e LABEL=baseline tests/load/noisy-neighbor.js
//   k6 run -e BASE_URL=... -e MODE=flood -e LABEL=flood-limits-on tests/load/noisy-neighbor.js
//   k6 run -e BASE_URL=... -e MODE=flood -e LABEL=flood-limits-off tests/load/noisy-neighbor.js   (RateLimits__Enabled=false)
//
// Tenant B (demo-forge, Pro) sends a steady 10 req/s to its storefront's program list; that request
// goes through catalog-api to the shared PostgreSQL server. In flood mode tenant A (demo-stride,
// Free: 20 req/s) sends the same request at 300 req/s. The run reports tenant B's p95 latency and
// error rate, and how many of tenant A's requests the gateway refused with 429.
import http from 'k6/http'
import { sleep } from 'k6'
import { Rate, Trend } from 'k6/metrics'

const BASE_URL = __ENV.BASE_URL
const MODE = __ENV.MODE || 'baseline'
const LABEL = __ENV.LABEL || MODE
const DURATION = __ENV.DURATION || '2m'
const FLOOD_RATE = Number(__ENV.FLOOD_RATE || 300)
const TENANT_A = 'demo-stride'
const TENANT_B = 'demo-forge'

if (!BASE_URL) throw new Error('Set BASE_URL to the staging gateway, for example -e BASE_URL=https://ca-athlos-gateway-staging...')

const bLatency = new Trend('tenant_b_duration', true)
const bErrors = new Rate('tenant_b_errors')
const aRejected = new Rate('tenant_a_rejected')
const aErrors = new Rate('tenant_a_errors')

const scenarios = {
  tenantB: {
    executor: 'constant-arrival-rate',
    exec: 'tenantB',
    rate: 10,
    timeUnit: '1s',
    duration: DURATION,
    preAllocatedVUs: 20,
    maxVUs: 200,
  },
}
if (MODE === 'flood') {
  scenarios.tenantA = {
    executor: 'constant-arrival-rate',
    exec: 'tenantA',
    rate: FLOOD_RATE,
    timeUnit: '1s',
    duration: DURATION,
    preAllocatedVUs: 100,
    maxVUs: 1000,
  }
}

export const options = {
  scenarios,
  // Cold starts from zero replicas take most of a minute (design §15.3); setup waits them out.
  setupTimeout: '4m',
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
}

const programs = (slug) => `${BASE_URL}/api/v1/marketplace/programs?coach=${slug}&pageSize=12`

// A slow answer is the measurement, not an error: wait up to 30 s for it.
const params = (tenant) => ({ tags: { tenant }, timeout: '30s' })

// The apps scale to zero when idle; wait until both coaches answer quickly, so cold starts are not
// part of the measurement.
export function setup() {
  const deadline = Date.now() + 180_000
  while (Date.now() < deadline) {
    const a = http.get(programs(TENANT_A))
    const b = http.get(programs(TENANT_B))
    if (a.status === 200 && b.status === 200 && a.timings.duration < 1000 && b.timings.duration < 1000) {
      // Let tenant A's bucket refill, so the warm-up does not spend its first second of budget.
      sleep(2)
      return
    }
    sleep(1)
  }
  throw new Error('The staging apps did not warm up within 3 minutes.')
}

export function tenantB() {
  const response = http.get(programs(TENANT_B), params('b'))
  bLatency.add(response.timings.duration)
  bErrors.add(response.status !== 200)
}

export function tenantA() {
  const response = http.get(programs(TENANT_A), params('a'))
  aRejected.add(response.status === 429)
  aErrors.add(response.status !== 200 && response.status !== 429)
}

export function handleSummary(data) {
  const m = data.metrics
  const value = (name, stat) => (m[name] ? m[name].values[stat] : undefined)
  const result = {
    label: LABEL,
    mode: MODE,
    duration: DURATION,
    floodRate: MODE === 'flood' ? FLOOD_RATE : 0,
    finishedAt: new Date().toISOString(),
    tenantB: {
      requests: value('tenant_b_errors', 'passes') + value('tenant_b_errors', 'fails'),
      p50Ms: value('tenant_b_duration', 'p(50)'),
      p95Ms: value('tenant_b_duration', 'p(95)'),
      p99Ms: value('tenant_b_duration', 'p(99)'),
      errorRate: value('tenant_b_errors', 'rate'),
    },
    tenantA:
      MODE === 'flood'
        ? {
            requests: value('tenant_a_rejected', 'passes') + value('tenant_a_rejected', 'fails'),
            rejectedRate: value('tenant_a_rejected', 'rate'),
            errorRate: value('tenant_a_errors', 'rate'),
          }
        : null,
  }
  const line = JSON.stringify(result, null, 2)
  return {
    stdout: `\n${line}\n`,
    [`tests/load/results/${LABEL}.json`]: line,
  }
}
