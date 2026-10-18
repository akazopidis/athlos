// The marketplace under a steady, realistic read load (design §15.3: gateway p95 < 300 ms, warm, normal
// load), run against staging with the demo coaches (design §15.2):
//
//   k6 run -e BASE_URL=https://<staging gateway> -e LABEL=marketplace-v1.8.0 tests/load/marketplace.js
//
// The same visitor traffic the shop sends, at RATE requests per second (20 by default) for DURATION:
//   40%  catalog search, with a random mix of text, category, price range, length, coach and sort
//        (catalog-api: filters, sorting, the coach facet; the cross-coach search is limited per client)
//   20%  a coach's storefront program list (?coach=)
//   20%  a program page
//   10%  the coach lookup the product cards use (tenancy-api, several coaches at once)
//   10%  a storefront header (tenancy-api)
// Each request is tagged with its kind, and the summary reports p50, p95 and p99 per kind. At 20 req/s
// the cross-coach part stays under the per-client limit of 20 req/s (design §4), so 429s mean a problem.
import http from 'k6/http'
import { sleep } from 'k6'
import { Rate, Trend } from 'k6/metrics'

const BASE_URL = __ENV.BASE_URL
const LABEL = __ENV.LABEL || 'marketplace'
const RATE = Number(__ENV.RATE || 20)
const DURATION = __ENV.DURATION || '2m'

if (!BASE_URL) throw new Error('Set BASE_URL to the staging gateway, for example -e BASE_URL=https://ca-athlos-gateway-staging...')

const coaches = {
  'demo-forge': ['strong-start', 'barbell-basics', 'kettlebell-engine', 'powerbuilding', 'upper-lower', 'hybrid-athlete', 'deadlift-peak', 'olympic-lifting'],
  'demo-stride': ['first-5k', '10k-faster', 'half-marathon', 'marathon-sub4', 'trail-ready'],
  'demo-flow': ['daily-mobility', 'desk-reset', 'splits-progression', 'handstand-path', 'yoga-for-lifters'],
}
const slugs = Object.keys(coaches)
const categories = ['Bodybuilding', 'Powerlifting', 'CrossFit', 'Weightlifting', 'Calisthenics', 'Running', 'Mobility', 'GeneralFitness']
const words = ['strength', 'run', 'mobility', 'marathon', 'barbell', 'yoga']
const sorts = ['Newest', 'PriceLowToHigh', 'PriceHighToLow']
const kinds = ['search', 'storefront_list', 'program_page', 'coach_lookup', 'storefront']

const durations = Object.fromEntries(kinds.map((k) => [k, new Trend(`${k}_duration`, true)]))
const errors = new Rate('errors')
const limited = new Rate('rate_limited')

export const options = {
  scenarios: {
    visitors: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 20,
      maxVUs: 200,
    },
  },
  // Cold starts from zero replicas take most of a minute (design §15.3); setup waits them out.
  setupTimeout: '4m',
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
}

const pick = (list) => list[Math.floor(Math.random() * list.length)]
const maybe = (p) => Math.random() < p

function searchUrl() {
  const query = []
  if (maybe(0.3)) query.push(`Q=${pick(words)}`)
  if (maybe(0.4)) query.push(`Category=${pick(categories)}`)
  if (maybe(0.3)) query.push(`MinPrice=1000&MaxPrice=${pick([3000, 5000, 8000])}`)
  if (maybe(0.2)) query.push(`MinWeeks=5&MaxWeeks=12`)
  if (maybe(0.5)) query.push(`Sort=${pick(sorts)}`)
  if (maybe(0.2)) query.push('Page=2')
  query.push('PageSize=12')
  return `${BASE_URL}/api/v1/marketplace/programs?${query.join('&')}`
}

function request() {
  const roll = Math.random()
  const coach = pick(slugs)
  if (roll < 0.4) return ['search', searchUrl()]
  if (roll < 0.6) return ['storefront_list', `${BASE_URL}/api/v1/marketplace/programs?coach=${coach}&PageSize=12`]
  if (roll < 0.8) return ['program_page', `${BASE_URL}/api/v1/marketplace/coaches/${coach}/programs/${pick(coaches[coach])}`]
  if (roll < 0.9) return ['coach_lookup', `${BASE_URL}/api/v1/marketplace/coaches?${slugs.map((s) => `slug=${s}`).join('&')}`]
  return ['storefront', `${BASE_URL}/api/v1/marketplace/coaches/${coach}`]
}

// The apps scale to zero when idle; wait until every service on the path answers quickly, so cold
// starts are not part of the measurement.
export function setup() {
  const deadline = Date.now() + 180_000
  while (Date.now() < deadline) {
    const answers = [
      http.get(`${BASE_URL}/api/v1/marketplace/programs?PageSize=1`),
      http.get(`${BASE_URL}/api/v1/marketplace/coaches/demo-forge`),
      http.get(`${BASE_URL}/api/v1/marketplace/coaches/demo-forge/programs/strong-start`),
    ]
    if (answers.every((r) => r.status === 200 && r.timings.duration < 1000)) {
      sleep(2)
      return
    }
    sleep(1)
  }
  throw new Error('The staging apps did not warm up within 3 minutes.')
}

export default function () {
  const [kind, url] = request()
  const response = http.get(url, { tags: { kind }, timeout: '30s' })
  durations[kind].add(response.timings.duration)
  limited.add(response.status === 429)
  errors.add(response.status !== 200 && response.status !== 429)
}

export function handleSummary(data) {
  const m = data.metrics
  const value = (name, stat) => (m[name] ? m[name].values[stat] : undefined)
  const result = {
    label: LABEL,
    rate: RATE,
    duration: DURATION,
    finishedAt: new Date().toISOString(),
    requests: value('errors', 'passes') + value('errors', 'fails'),
    errorRate: value('errors', 'rate'),
    rateLimitedRate: value('rate_limited', 'rate'),
    overall: { p50Ms: value('http_req_duration', 'p(50)'), p95Ms: value('http_req_duration', 'p(95)'), p99Ms: value('http_req_duration', 'p(99)') },
    byKind: Object.fromEntries(
      kinds.map((k) => [k, { p50Ms: value(`${k}_duration`, 'p(50)'), p95Ms: value(`${k}_duration`, 'p(95)'), p99Ms: value(`${k}_duration`, 'p(99)') }]),
    ),
  }
  const line = JSON.stringify(result, null, 2)
  return {
    stdout: `\n${line}\n`,
    [`tests/load/results/${LABEL}.json`]: line,
  }
}
