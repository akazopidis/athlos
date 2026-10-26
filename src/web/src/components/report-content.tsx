import { useMutation } from '@tanstack/react-query'
import { Flag } from 'lucide-react'
import { useState } from 'react'
import { selectClassName } from '@/components/field'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'

const Reasons = [
  { value: 'Illegal', label: 'Illegal or dangerous' },
  { value: 'Misleading', label: 'Misleading claims' },
  { value: 'Copyright', label: "Someone else's work" },
  { value: 'Abusive', label: 'Abusive or hateful' },
  { value: 'Other', label: 'Something else' },
] as const

type Reason = (typeof Reasons)[number]['value']

type Target = { kind: 'Program'; programId: string } | { kind: 'Coach'; coachSlug: string }

// Reporting a program or a coach to the platform admin (design §2.4, the notice the EU Digital
// Services Act asks for), on program pages and storefronts. Signed out, it is not offered.
export function ReportContent({ target, label }: { target: Target; label: string }) {
  const { api, auth } = useApp()
  const [open, setOpen] = useState(false)
  const [reason, setReason] = useState<Reason>('Misleading')
  const [note, setNote] = useState('')
  const report = useMutation({
    mutationFn: async () => {
      const { response } = await api.POST('/api/v1/me/reports', {
        body: {
          kind: target.kind,
          programId: target.kind === 'Program' ? target.programId : null,
          coachSlug: target.kind === 'Coach' ? target.coachSlug : null,
          reason,
          note: note.trim() || null,
        },
      })
      if (!response.ok) throw new Error('Could not send the report. Try again.')
    },
  })

  if (!auth?.account) return null
  if (report.isSuccess) return <p className="text-xs text-muted-foreground" role="status">Thanks, the Athlos team will take a look.</p>
  if (!open) {
    return (
      <Button variant="ghost" size="sm" className="h-7 px-2 text-xs text-muted-foreground" onClick={() => setOpen(true)}>
        <Flag aria-hidden /> {label}
      </Button>
    )
  }

  return (
    <form
      className="flex flex-wrap items-end gap-2 rounded-xl border p-3"
      aria-label={label}
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
