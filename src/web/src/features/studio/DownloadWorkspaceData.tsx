import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Download } from 'lucide-react'
import { DataExportList } from '@/components/data-exports'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'

// The owner's workspace export (design §5.4): the workspace and team, programs with their workouts,
// sales and discount codes, customers per program and reviews, as one JSON file. Asking queues it;
// when it is ready it is listed here and in Account settings, and emailed, and kept for seven days.
export function DownloadWorkspaceData({ slug }: { slug: string }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const prepare = useMutation({
    mutationFn: async () => {
      const { data, error } = await api.POST('/api/v1/studio/{tenantSlug}/workspace/exports', { params: { path: { tenantSlug: slug } } })
      if (!data) throw new Error(problemTitle(error) ?? "The workspace's data could not be requested just now. Try again in a moment.")
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['data-exports'] }),
  })

  return (
    <div className="space-y-3 rounded-2xl border bg-card p-4">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div className="min-w-0">
          <p className="font-semibold">Workspace data</p>
          <p className="text-sm text-muted-foreground">
            Your programs and workouts, sales and discount codes, reviews and team, in one file. It holds your customers' addresses: keep it safe.
          </p>
        </div>
        <Button variant="outline" onClick={() => prepare.mutate()} disabled={prepare.isPending}>
          <Download aria-hidden /> {prepare.isPending ? 'Asking…' : 'Prepare workspace data'}
        </Button>
      </div>
      <DataExportList workspace={slug} />
      {prepare.error && (
        <Alert variant="destructive" role="alert">
          <AlertDescription>{prepare.error.message}</AlertDescription>
        </Alert>
      )}
    </div>
  )
}
