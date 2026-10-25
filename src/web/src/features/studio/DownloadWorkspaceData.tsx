import { useMutation } from '@tanstack/react-query'
import { Download } from 'lucide-react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'

// The owner's workspace export (design §5.4): the workspace and team, programs with their workouts,
// sales and discount codes, customers per program and reviews, as one JSON file saved by the browser.
export function DownloadWorkspaceData({ slug }: { slug: string }) {
  const { api } = useApp()
  const download = useMutation({
    mutationFn: async () => {
      const { data, error } = await api.GET('/api/v1/studio/{tenantSlug}/workspace/export', { params: { path: { tenantSlug: slug } } })
      if (!data) throw new Error(problemTitle(error) ?? "The workspace's data could not be gathered just now. Try again in a moment.")
      const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }))
      const link = document.createElement('a')
      link.href = url
      link.download = `athlos-${slug}-${new Date().toISOString().slice(0, 10)}.json`
      link.click()
      URL.revokeObjectURL(url)
    },
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
        <Button variant="outline" onClick={() => download.mutate()} disabled={download.isPending}>
          <Download aria-hidden /> {download.isPending ? 'Gathering…' : 'Download workspace data'}
        </Button>
      </div>
      {download.error && (
        <Alert variant="destructive" role="alert">
          <AlertDescription>{download.error.message}</AlertDescription>
        </Alert>
      )}
    </div>
  )
}
