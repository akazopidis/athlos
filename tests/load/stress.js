// Stress (design §15.3): one coach's storefront traffic stepped up past what its plan allows, to see
// where the platform stops serving and how: latency of the requests it serves, 429s from the rate
// limiter, and any 5xx. Run against staging with the Pro demo coach (100 req/s, design §4):
//
//   k6 run -e BASE_URL=https://<staging gateway> -e LABEL=stress-v1.12.0 tests/load/stress.js
//
// Steps of STEP_DURATION each (60 s by default) at 25, 50, 100, 150 and 200 req/s, ramping 10 s
// between them. All of it is the coach's own storefront traffic (program list, program pages and the
// storefront header), so it spends that coach's token bucket, not the per-client one. The summary
// reports, per step, the p50/p95/p99 of the requests served and the share answered 429 or 5xx.
import http from 'k6/http'
import { sleep } from 'k6'
import { Rate, Trend } from 'k6/metrics'

const BASE_URL = __ENV.BASE_URL
const LABEL = __ENV.LABEL || 'stress'
const COACH = __ENV.COACH || 'demo-forge'
const STEP_SECONDS = Number(__ENV.STEP_DURATION_SECONDS || 60)
const RAMP_SECONDS = 10
const RATES = [25, 50, 100, 150, 200]
const PROGRAMS = ['strong-start', 'barbell-basics', 'kettlebell-engine', 'powerbuilding', 'upper-lower', 'hybrid-athlete', 'deadlift-peak', 'olympic-lifting']

if (!BASE_URL) throw new Error('Set BASE_URL to the staging gateway.')

const served = Object.fromEntries(RATES.map((r) => [r, new Trend(`served_${r}_duration`, true)]))
const limited = Object.fromEntries(RATES.map((r) => [r, new Rate(`limited_${r}`)]))
const failed = Object.fromEntries(RATES.map((r) => [r, new Rate(`failed_${r}`)]))

const stages = RATES.flatMap((target) => [
  { target, duration: `${RAMP_SECONDS}s` },
  { target, duration: `${STEP_SECONDS}s` },
])

export const options = {
  scenarios: {
    storefront: {
      executor: 'ramping-arrival-rate',
      startRate: RATES[0],
      timeUnit: '1s',
      preAllocatedVUs: 50,
      maxVUs: 600,
      stages,
    },
  },
  setupTimeout: '4m',
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
}

const pick = (list) => list[Math.floor(Math.random() * list.length)]

// The step a request belongs to, from the time since the scenario started; ramps count for none.
function stepOf(elapsedSeconds) {
  let t = 0
  for (const rate of RATES) {
    t += RAMP_SECONDS
    if (elapsedSeconds < t) return null
    t += STEP_SECONDS
    if (elapsedSeconds < t) return rate
  }
  return null
}

export function setup() {
  const deadline = Date.now() + 180_000
  while (Date.now() < deadline) {
    const answers = [
      http.get(`${BASE_URL}/api/v1/marketplace/programs?coach=${COACH}&PageSize=12`),
      http.get(`${BASE_URL}/api/v1/marketplace/coaches/${COACH}`),
      http.get(`${BASE_URL}/api/v1/marketplace/coaches/${COACH}/programs/strong-start`),
    ]
    if (answers.every((r) => r.status === 200 && r.timings.duration < 1000)) {
      sleep(2)
      return { started: Date.now() }
    }
    sleep(1)
  }
  throw new Error('The staging apps did not warm up within 3 minutes.')
}

export default function (data) {
  const roll = Math.random()
  const url =
    roll < 0.4
      ? `${BASE_URL}/api/v1/marketplace/programs?coach=${COACH}&PageSize=12`
      : roll < 0.8
        ? `${BASE_URL}/api/v1/marketplace/coaches/${COACH}/programs/${pick(PROGRAMS)}`
        : `${BASE_URL}/api/v1/marketplace/coaches/${COACH}`
  const response = http.get(url, { timeout: '30s' })
  const step = stepOf((Date.now() - data.started) / 1000)
  if (step === null) return
  limited[step].add(response.status === 429)
  failed[step].add(response.status >= 500 || response.status === 0)
  if (response.status === 200) served[step].add(response.timings.duration)
}

export function handleSummary(data) {
  const m = data.metrics
  const value = (name, stat) => (m[name] ? m[name].values[stat] : undefined)
  const result = {
    label: LABEL,
    coach: COACH,
    stepSeconds: STEP_SECONDS,
    finishedAt: new Date().toISOString(),
    steps: RATES.map((rate) => ({
      rate,
      requests: (value(`limited_${rate}`, 'passes') ?? 0) + (value(`limited_${rate}`, 'fails') ?? 0),
      limitedRate: value(`limited_${rate}`, 'rate'),
      failedRate: value(`failed_${rate}`, 'rate'),
      served: { p50Ms: value(`served_${rate}_duration`, 'p(50)'), p95Ms: value(`served_${rate}_duration`, 'p(95)'), p99Ms: value(`served_${rate}_duration`, 'p(99)') },
    })),
  }
  const line = JSON.stringify(result, null, 2)
  return { stdout: `\n${line}\n`, [`tests/load/results/${LABEL}.json`]: line }
}
