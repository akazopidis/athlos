import type { PublicClientApplication } from '@azure/msal-browser'
import createClient, { type Middleware } from 'openapi-fetch'
import { getAccessToken } from '@/lib/auth'
import type { paths as catalogPaths } from './catalog.ts'
import type { paths as enrollmentPaths } from './enrollment.ts'
import type { paths as orderingPaths } from './ordering.ts'
import type { paths as tenancyPaths } from './tenancy.ts'

// One client over every service's routes: the gateway serves them all from one origin.
type paths = tenancyPaths & catalogPaths & orderingPaths & enrollmentPaths

// Typed client generated from the services' OpenAPI documents (npm run generate:api). Every call
// goes to the gateway's origin; the gateway routes it to the right service.
export function createApi(msal: PublicClientApplication | null, apiScope: string | null) {
  const client = createClient<paths>({ baseUrl: window.location.origin })

  const bearer: Middleware = {
    async onRequest({ request }) {
      const token = await getAccessToken(msal, apiScope)
      if (token) request.headers.set('Authorization', `Bearer ${token}`)
      return request
    },
  }
  client.use(bearer)
  return client
}

export type Api = ReturnType<typeof createApi>
