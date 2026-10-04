import { useQuery } from '@tanstack/react-query'
import { useApp } from '@/lib/AppContext'

// A coach's public profile (name, logo, brand color). The storefront and its program pages share it.
export function useStorefront(slug: string) {
  const { api } = useApp()
  return useQuery({
    queryKey: ['storefront', slug],
    queryFn: async () => (await api.GET('/api/v1/marketplace/coaches/{slug}', { params: { path: { slug } } })).data ?? null,
  })
}
