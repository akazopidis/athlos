import { useMutation } from '@tanstack/react-query'
import { Flag } from 'lucide-react'
import { useState } from 'react'
import { selectClassName } from '@/components/field'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'

const Reasons = [
  { value: 'Abusive', label: 'Abusive or hateful' },
  { value: 'OffTopic', label: 'Not about the program' },
  { value: 'PersonalData', label: 'Shares personal details' },
  { value: 'Spam', label: 'Spam or advertising' },
  { value: 'Other', label: 'Something else' },
] as const

type Reason = (typeof Reasons)[number]['value']

// Reporting a review to the platform admin (design §2.4), for customers on a program's page and for
// coaches in their studio. Signed out, it is not offered.
export function ReportReview({ programId, reviewId }: { programId: string; reviewId: string }) {
  const { api, msal } = useApp()
  const [open, setOpen] = useState(false)
  const [reason, setReason] = useState<Reason>('Abusive')
  const [note, setNote] = useState('')
  const report = useMutation({
    mutationFn: async () => {
      const { response } = await api.POST('/api/v1/marketplace/programs/{programId}/reviews/{reviewId}/report', {
        params: { path: { programId, reviewId } },
        body: { reason, note: note.trim() || null },
      })
      if (!response.ok) throw new Error('Could not send the report. Try again.')
    },
  })

  if (!msal?.getActiveAccount()) return null
  if (report.isSuccess) return <p className="text-xs text-muted-foreground" role="status">Thanks, the Athlos team will take a look.</p>
  if (!open) {
    return (
      <Button variant="ghost" size="sm" className="h-7 px-2 text-xs text-muted-foreground" onClick={() => setOpen(true)}>
        <Flag aria-hidden /> Report
      </Button>
    )
  }

  return (
    <form
      className="flex flex-wrap items-end gap-2 rounded-xl border p-3"
      onSubmit={(e) => {
        e.preventDefault()
        report.mutate()
      }}
    >
      <label className="space-y-1 text-xs font-medium">
        <span className="block">Why?</span>
        <select className={`${selectClassName} h-9`} value={reason} onChange={(e) => setReason(e.target.value as Reason)}>
          {Reasons.map((r) => (
            <option key={r.value} value={r.value}>
              {r.label}
            </option>
          ))}
        </select>
      </label>
      <label className="min-w-40 flex-1 space-y-1 text-xs font-medium">
        <span className="block">Note (optional)</span>
        <input className={`${selectClassName} h-9`} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
      </label>
      <Button type="submit" size="sm" disabled={report.isPending}>
        {report.isPending ? 'Sending…' : 'Send report'}
      </Button>
      <Button type="button" size="sm" variant="ghost" onClick={() => setOpen(false)}>
        Cancel
      </Button>
      {report.error && (
        <p className="w-full text-xs text-destructive" role="alert">
          {report.error.message}
        </p>
      )}
    </form>
  )
}
