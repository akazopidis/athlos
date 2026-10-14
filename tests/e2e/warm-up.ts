// Wakes the environment before the tests: every service scales to zero, and a cold start takes up to
// about 90 s (design §15.3). The marketplace search touches the gateway and catalog-api; the coach
// lookup wakes tenancy-api.
export default async function warmUp() {
  const base = process.env.E2E_BASE_URL || 'https://ca-athlos-gateway-staging.ashybush-033a4967.italynorth.azurecontainerapps.io'
  const paths = ['/api/v1/marketplace/programs?pageSize=1', '/api/v1/marketplace/coaches?slug=demo-forge']
  const deadline = Date.now() + 240_000

  for (const path of paths) {
    for (;;) {
      try {
        const response = await fetch(new URL(path, base), { signal: AbortSignal.timeout(60_000) })
        if (response.ok) break
      } catch {
        // Not up yet.
      }
      if (Date.now() > deadline) throw new Error(`${base}${path} did not answer within 4 minutes`)
      await new Promise((resolve) => setTimeout(resolve, 5_000))
    }
  }
}
