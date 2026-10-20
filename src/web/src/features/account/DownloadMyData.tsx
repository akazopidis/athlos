import { useMutation } from '@tanstack/react-query'
import { Download } from 'lucide-react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'

// "Download my data" (design §5.4): everything Athlos holds about the user (profile, workspaces,
// orders, programs and progress, reviews, saved programs) as one JSON file, saved by the browser.
export function DownloadMyData() {
  const { api } = useApp()
  const download = useMutation({
    mutationFn: async () => {
      const { data, error } = await api.GET('/api/v1/me/export')
      if (!data) throw new Error(problemTitle(error) ?? 'Your data could not be gathered just now. Try again in a moment.')
      const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }))
      const link = document.createElement('a')
      link.href = url
      link.download = `athlos-data-${new Date().toISOString().slice(0, 10)}.json`
      link.click()
      URL.revokeObjectURL(url)
    },
  })

  return (
    <div className="space-y-3 rounded-2xl border bg-card p-4">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div className="min-w-0">
          <p className="font-semibold">Your data</p>
          <p className="text-sm text-muted-foreground">Everything Athlos holds about you: your account, orders, programs, progress, reviews and saved programs.</p>
        </div>
        <Button variant="outline" onClick={() => download.mutate()} disabled={download.isPending}>
          <Download aria-hidden /> {download.isPending ? 'Gathering…' : 'Download my data'}
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
