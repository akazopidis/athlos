import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router'
import { useApp } from '../../AppContext.tsx'

// What every studio page needs: the workspace slug from the route, the workspace itself and the
// caller's role there. The gateway checks membership; another tenant's workspace is a plain 404.
export function useStudio() {
  const { slug = '' } = useParams()
  const { api } = useApp()

  const workspace = useQuery({
    queryKey: ['workspace', slug],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/workspace', { params: { path: { tenantSlug: slug } } })).data ?? null,
  })

  const memberships = useQuery({
    queryKey: ['memberships'],
    queryFn: async () => (await api.GET('/api/v1/me/memberships')).data ?? [],
  })
  const isOwner = memberships.data?.some((m) => m.slug === slug && m.role === 'Owner') ?? false

  return { slug, workspace, isOwner }
}
