import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Download } from 'lucide-react'
import { DataExportList } from '@/components/data-exports'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'

// "Download my data" (design §5.4): everything Athlos holds about the user (profile, workspaces,
// orders, programs and progress, reviews, saved programs) as one JSON file. Asking queues it; when it
// is ready (usually seconds) it is listed here and emailed, and kept for seven days.
export function DownloadMyData() {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const prepare = useMutation({
    mutationFn: async () => {
      const { data, error } = await api.POST('/api/v1/me/exports')
      if (!data) throw new Error(problemTitle(error) ?? 'Your data could not be requested just now. Try again in a moment.')
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['data-exports'] }),
  })

  return (
    <div className="space-y-3 rounded-2xl border bg-card p-4">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div className="min-w-0">
          <p className="font-semibold">Your data</p>
          <p className="text-sm text-muted-foreground">Everything Athlos holds about you: your account, orders, programs, progress, reviews and saved programs.</p>
        </div>
        <Button variant="outline" onClick={() => prepare.mutate()} disabled={prepare.isPending}>
          <Download aria-hidden /> {prepare.isPending ? 'Asking…' : 'Prepare my data'}
        </Button>
      </div>
      <DataExportList />
      {prepare.error && (
        <Alert variant="destructive" role="alert">
          <AlertDescription>{prepare.error.message}</AlertDescription>
        </Alert>
      )}
    </div>
  )
}
