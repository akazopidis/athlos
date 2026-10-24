import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useApp } from './AppContext.tsx'

// The signed-in customer's saved programs (design §2.4, catalog-api's "/me/saved"): the list for
// "/me/saved", and the ids for the hearts on product cards and pages. Empty when signed out.
export function useSavedPrograms() {
  const { api, auth } = useApp()
  const signedIn = Boolean(auth?.account)
  const saved = useQuery({
    queryKey: ['saved'],
    queryFn: async () => (await api.GET('/api/v1/me/saved')).data ?? [],
    enabled: signedIn,
  })
  return { programs: saved.data, isPending: signedIn && saved.isPending, ids: new Set(saved.data?.map((p) => p.id) ?? []) }
}

// Saves or unsaves one program; the list is read again afterwards.
export function useSaveProgram() {
  const { api } = useApp()
  const client = useQueryClient()
  return useMutation({
    mutationFn: async ({ programId, save }: { programId: string; save: boolean }) => {
      const params = { params: { path: { programId } } }
      const { response } = save ? await api.PUT('/api/v1/me/saved/{programId}', params) : await api.DELETE('/api/v1/me/saved/{programId}', params)
      if (response.status === 404) throw new Error('This program is no longer for sale.')
      if (response.status === 409) throw new Error('You can save up to 100 programs. Remove one to save another.')
      if (!response.ok) throw new Error('Could not update your saved programs.')
    },
    onSettled: () => client.invalidateQueries({ queryKey: ['saved'] }),
  })
}
