import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { FormEvent } from 'react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Field } from '@/components/field'
import { Section } from '@/components/page'
import { useApp } from '../AppContext.tsx'
import { problemTitle } from '../problem.ts'

// The owner's team (design §5.2): invite staff by email, see open invites, revoke one.
// Shown to owners only; tenancy-api answers 403 to staff anyway.
export function StudioTeam({ slug }: { slug: string }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const path = { tenantSlug: slug }

  const invites = useQuery({
    queryKey: ['invites', slug],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/invites', { params: { path } })).data ?? [],
  })

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['invites', slug] })

  const invite = useMutation({
    mutationFn: async (email: string) => {
      const { data, error } = await api.POST('/api/v1/studio/{tenantSlug}/invites', { params: { path }, body: { email } })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not send the invite.')
    },
    onSuccess: refresh,
  })

  const revoke = useMutation({
    mutationFn: async (inviteId: string) => {
      const { response } = await api.DELETE('/api/v1/studio/{tenantSlug}/invites/{inviteId}', { params: { path: { ...path, inviteId } } })
      if (!response.ok) throw new Error('Could not revoke the invite.')
    },
    onSuccess: refresh,
  })

  const onInvite = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    const email = String(new FormData(form).get('email') ?? '').trim()
    invite.mutate(email, { onSuccess: () => form.reset() })
  }

  return (
    <Section title="Team">
      <Card>
        <CardContent className="space-y-4">
          <form onSubmit={onInvite} className="flex flex-wrap items-end gap-3">
            <Field id="invite-email" label="Invite staff by email" hint="They get an email with a link, valid for 7 days, and join as staff." className="min-w-64 flex-1">
              <Input id="invite-email" name="email" type="email" required maxLength={320} />
            </Field>
            <Button type="submit" disabled={invite.isPending} className="mb-6">
              Send invite
            </Button>
          </form>
          {invite.isSuccess && (
            <Alert>
              <AlertDescription>Invite sent.</AlertDescription>
            </Alert>
          )}
          {invite.error && (
            <Alert variant="destructive">
              <AlertDescription>{invite.error.message}</AlertDescription>
            </Alert>
          )}
          {invites.data && invites.data.length > 0 && (
            <div className="space-y-2">
              <h3 className="text-sm font-medium">Open invites</h3>
              <ul className="divide-y rounded-lg border">
                {invites.data.map((i) => (
                  <li key={i.id} className="flex flex-wrap items-center justify-between gap-2 px-4 py-3 text-sm">
                    <span>{i.email}</span>
                    <span className="flex items-center gap-3">
                      {i.expired ? (
                        <Badge variant="destructive">Expired</Badge>
                      ) : (
                        <span className="text-muted-foreground">valid until {new Date(i.expiresAt).toLocaleDateString()}</span>
                      )}
                      <Button size="sm" variant="ghost" onClick={() => revoke.mutate(i.id)} disabled={revoke.isPending}>
                        Revoke
                      </Button>
                    </span>
                  </li>
                ))}
              </ul>
              {revoke.error && <p className="text-sm text-destructive">{revoke.error.message}</p>}
            </div>
          )}
        </CardContent>
      </Card>
    </Section>
  )
}
