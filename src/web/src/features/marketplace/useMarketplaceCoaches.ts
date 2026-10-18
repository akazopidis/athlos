import { useQuery } from '@tanstack/react-query'
import { useApp } from '@/lib/AppContext'
import { useCoaches, type Coach } from '@/lib/useCoaches'

export type MarketplaceCoach = Coach & { programs: number }

// Coaches with programs on sale, most programs first: catalog's coach facet over the whole marketplace,
// with each coach's profile from tenancy-api (design §8). A coach without published programs is not
// listed: there is nothing to buy from them yet.
export function useMarketplaceCoaches(limit?: number): { coaches: MarketplaceCoach[]; isPending: boolean } {
  const { api } = useApp()
  const facet = useQuery({
    queryKey: ['marketplace-coaches'],
    queryFn: async () => (await api.GET('/api/v1/marketplace/programs', { params: { query: { PageSize: 1 } } })).data?.coaches ?? [],
    staleTime: 5 * 60_000,
  })
  const ranked = [...(facet.data ?? [])].sort((a, b) => Number(b.count) - Number(a.count)).slice(0, limit ?? 50)
  const profiles = useCoaches(ranked.map((c) => c.coachSlug))
  // A suspended coach has no storefront: tenancy leaves them out, and so does this list.
  const coaches = ranked.flatMap((c) => {
    const profile = profiles.get(c.coachSlug)
    return profile ? [{ ...profile, programs: Number(c.count) }] : []
  })
  return { coaches, isPending: facet.isPending || (ranked.length > 0 && profiles.size === 0) }
}

