import { useMutation, useQuery } from '@tanstack/react-query'
import { Download, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'

// The data exports the signed-in user asked for (design §5.4): prepared after the request, then kept
// for seven days. The list is re-read every few seconds while one is being prepared; a ready one
// downloads through tenancy-api, signed in. `workspace` narrows it to that workspace's exports.
function useDataExports() {
  const { api } = useApp()
  return useQuery({
    queryKey: ['data-exports'],
    queryFn: async () => (await api.GET('/api/v1/me/exports')).data ?? [],
    refetchInterval: (query) => (query.state.data?.some((e) => e.status === 'Preparing') ? 3000 : false),
  })
}

export function DataExportList({ workspace }: { workspace?: string }) {
  const { api } = useApp()
  const exports = useDataExports()
  const shown = (exports.data ?? []).filter((e) => !workspace || e.workspace === workspace)
  const download = useMutation({
    mutationFn: async ({ id, name }: { id: string; name: string }) => {
      const { data, response } = await api.GET('/api/v1/me/exports/{exportId}/file', { params: { path: { exportId: id } }, parseAs: 'blob' })
      if (!response.ok || !data) throw new Error('The file is no longer available. Prepare a new one.')
      const url = URL.createObjectURL(data)
      const link = document.createElement('a')
      link.href = url
      link.download = name
      link.click()
      URL.revokeObjectURL(url)
    },
  })

  if (shown.length === 0) return null
  return (
    <ul className="space-y-2">
      {shown.map((e) => {
        const day = (e.readyAt ?? e.requestedAt).slice(0, 10)
        const label = e.kind === 'Workspace' ? `Workspace data (${e.workspace})` : 'Your data'
        return (
          <li key={e.id} className="flex flex-wrap items-center justify-between gap-3 rounded-xl border px-3 py-2 text-sm">
            <span className="min-w-0">
              <span className="font-medium">{label}</span>{' '}
              <span className="text-muted-foreground">
                {e.status === 'Ready'
                  ? `ready, kept until ${new Date(e.expiresAt!).toLocaleDateString()}`
                  : e.status === 'Failed'
                    ? 'could not be prepared; ask again'
                    : 'being prepared; we will email you'}
              </span>
            </span>
            {e.status === 'Ready' ? (
              <Button size="sm" variant="outline" disabled={download.isPending} onClick={() => download.mutate({ id: e.id, name: `athlos-${e.workspace ?? 'data'}-${day}.json` })}>
                <Download aria-hidden /> Download
              </Button>
            ) : e.status === 'Preparing' ? (
              <Loader2 className="size-4 animate-spin text-muted-foreground" aria-label="Being prepared" />
            ) : null}
          </li>
        )
      })}
      {download.error && (
        <li className="text-sm text-destructive" role="alert">
          {download.error.message}
        </li>
      )}
    </ul>
  )
}
