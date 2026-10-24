import { useQuery } from '@tanstack/react-query'
import { useApp } from './AppContext.tsx'

// The addresses of the workspaces the signed-in user coaches in (owner or staff). The shop shows
// their programs as "Your program" instead of offering to buy them: ordering-api refuses that
// checkout too (design §2.2). Empty when signed out.
export function useMyWorkspaces(): Set<string> {
  const { api, auth } = useApp()
  const signedIn = Boolean(auth?.account)
  const memberships = useQuery({
    queryKey: ['memberships'],
    queryFn: async () => (await api.GET('/api/v1/me/memberships')).data ?? [],
    enabled: signedIn,
  })
  return new Set((memberships.data ?? []).map((m) => m.slug))
}

// True while a signed-in user's workspaces are still loading: a page that offers different actions to
// a coach (their own program) waits, instead of first offering to buy it.
export function useMyWorkspacesPending(): boolean {
  const { api, auth } = useApp()
  const signedIn = Boolean(auth?.account)
  const memberships = useQuery({
    queryKey: ['memberships'],
    queryFn: async () => (await api.GET('/api/v1/me/memberships')).data ?? [],
    enabled: signedIn,
  })
  return signedIn && memberships.isPending
}
