import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { CoachTermsDate, CoachTermsVersion } from '@/lib/legal'
import { useStudio } from './useStudio.ts'

// The Terms for coaches changed since the workspace accepted them, or it was opened before there were
// any (design §5.4). The owner accepts the version shown, recorded like the acceptance at opening;
// staff are told the owner needs to. Selling goes on meanwhile.
export function CoachTermsNotice() {
  const { slug, workspace, isOwner } = useStudio()
  const { api } = useApp()
  const queryClient = useQueryClient()
  const [accepted, setAccepted] = useState(false)

  const accept = useMutation({
    mutationFn: async () => {
      const { error, response } = await api.POST('/api/v1/studio/{tenantSlug}/workspace/coach-terms', {
        params: { path: { tenantSlug: slug } },
        body: { termsVersion: CoachTermsVersion },
      })
      if (response.status === 409 && (error as { reason?: string } | undefined)?.reason === 'terms') {
        throw new Error('The Terms for coaches have changed again. Reload the page to read and accept the new version.')
      }
      if (!response.ok) throw new Error('Could not record your acceptance. Try again.')
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['workspace', slug] }),
  })

  if (!workspace.data || workspace.data.coachTermsCurrent) return null

  return (
    <Alert>
      <AlertTitle>New Terms for coaches</AlertTitle>
      <AlertDescription className="space-y-3">
        <p>
          The{' '}
          <Link to="/legal/coaches" target="_blank" className="font-medium underline underline-offset-4">
            Terms for coaches
          </Link>{' '}
          of {CoachTermsDate} apply to selling on Athlos.{' '}
          {isOwner ? 'Please read them and accept them for this workspace.' : 'The owner of this workspace needs to accept them.'}
        </p>
        {isOwner && (
          <form
            className="flex flex-wrap items-center gap-3"
            onSubmit={(e) => {
              e.preventDefault()
              accept.mutate()
            }}
          >
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" required checked={accepted} onChange={(e) => setAccepted(e.target.checked)} className="size-4 accent-primary" />
              I accept the Terms for coaches.
            </label>
            <Button type="submit" size="sm" disabled={!accepted || accept.isPending}>
              Accept
            </Button>
          </form>
        )}
        {accept.error && <p className="text-sm text-destructive">{accept.error.message}</p>}
      </AlertDescription>
    </Alert>
  )
}
