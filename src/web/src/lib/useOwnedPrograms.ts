import { useQuery } from '@tanstack/react-query'
import { useApp } from './AppContext.tsx'

// The ids of the programs the signed-in customer owns (enrollment-api's "my programs"), for the
// shop's "Owned" badges and to keep owned programs out of the cart. Empty when signed out.
export function useOwnedPrograms(): Set<string> {
  const { api, auth } = useApp()
  const signedIn = Boolean(auth?.account)
  const owned = useQuery({
    queryKey: ['my-programs'],
    queryFn: async () => (await api.GET('/api/v1/me/programs')).data ?? null,
    enabled: signedIn,
  })
  return new Set(owned.data?.items.map((p) => p.programId) ?? [])
}

// True while a signed-in customer's programs are still loading (see useMyWorkspacesPending).
export function useOwnedProgramsPending(): boolean {
  const { api, auth } = useApp()
  const signedIn = Boolean(auth?.account)
  const owned = useQuery({
    queryKey: ['my-programs'],
    queryFn: async () => (await api.GET('/api/v1/me/programs')).data ?? null,
    enabled: signedIn,
  })
  return signedIn && owned.isPending
}
