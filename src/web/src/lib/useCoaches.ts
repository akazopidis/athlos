import { useQuery } from '@tanstack/react-query'
import type { components } from '@/api/tenancy'
import { useApp } from './AppContext.tsx'

export type Coach = components['schemas']['GetStorefrontResponse']

// Coaches' names and logos by address (tenancy-api's storefront lookup), for product cards, the coach
// filter and the cart: catalog knows a program's coach only by slug. Missing coaches (suspended) are
// simply absent; callers fall back to the slug.
export function useCoaches(slugs: string[]): Map<string, Coach> {
  const { api } = useApp()
  const unique = [...new Set(slugs)].sort()
  const coaches = useQuery({
    queryKey: ['coaches', unique],
    queryFn: async () => (await api.GET('/api/v1/marketplace/coaches', { params: { query: { slug: unique } } })).data ?? [],
    enabled: unique.length > 0,
    staleTime: 5 * 60_000,
  })
  return new Map((coaches.data ?? []).map((c) => [c.slug, c]))
}
