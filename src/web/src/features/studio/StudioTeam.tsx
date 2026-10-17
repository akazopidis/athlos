import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Field } from '@/components/field'
import { Section } from '@/components/page'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'

type Action = { userId: string; kind: 'remove' | 'leave' | 'owner' }

// The team (design §5.2). Everyone sees the members; the owner also invites staff, removes them and can
// hand ownership to one (becoming staff), and a staff member can leave. A workspace always has exactly
// one owner, so the owner cannot leave. Handing over disconnects the owner's Stripe account (sales
// pause until the new owner sets up payouts). Role changes reach the gateway within a minute.
export function StudioTeam({ slug, isOwner }: { slug: string; isOwner: boolean }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const path = { tenantSlug: slug }
  const [confirming, setConfirming] = useState<Action | null>(null)

  const members = useQuery({
    queryKey: ['members', slug],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/studio/{tenantSlug}/members', { params: { path } })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not load the team.')
      return data
    },
  })

  const invites = useQuery({
    queryKey: ['invites', slug],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/invites', { params: { path } })).data ?? [],
    enabled: isOwner,
  })

  const change = useMutation({
    mutationFn: async ({ userId, kind }: Action) => {
      const { error, response } =
        kind === 'owner'
          ? await api.POST('/api/v1/studio/{tenantSlug}/members/{userId}/ownership', { params: { path: { ...path, userId } } })
          : await api.DELETE('/api/v1/studio/{tenantSlug}/members/{userId}', { params: { path: { ...path, userId } } })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not change the team.')
    },
    onSuccess: async (_, { kind }) => {
      setConfirming(null)
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['members', slug] }),
        queryClient.invalidateQueries({ queryKey: ['memberships'] }),
      ])
      if (kind === 'leave') navigate('/me/workspaces')
    },
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
          {members.data && (
            <ul className="divide-y rounded-lg border">
              {members.data.map((m) => {
                const action: Action | null = m.isYou
                  ? m.role === 'Staff'
                    ? { userId: m.userId, kind: 'leave' }
                    : null
                  : isOwner && m.role === 'Staff'
                    ? { userId: m.userId, kind: 'remove' }
                    : null
                const asking = confirming?.userId === m.userId ? confirming : null
                return (
                  <li key={m.userId} className="flex flex-wrap items-center justify-between gap-2 px-4 py-3 text-sm">
                    <span className="min-w-0">
                      <span className="block truncate font-medium">
                        {m.displayName}
                        {m.isYou && <span className="font-normal text-muted-foreground"> (you)</span>}
                      </span>
                      {m.email && <span className="block truncate text-muted-foreground">{m.email}</span>}
                    </span>
                    <span className="flex items-center gap-2">
                      <Badge variant={m.role === 'Owner' ? 'default' : 'outline'}>{m.role}</Badge>
                      {asking ? (
                        <>
                          <Button size="sm" variant="destructive" disabled={change.isPending} onClick={() => change.mutate(asking)}>
                            {asking.kind === 'owner' ? 'Make owner, become staff' : asking.kind === 'leave' ? 'Leave workspace' : 'Remove'}
                          </Button>
                          <Button size="sm" variant="ghost" disabled={change.isPending} onClick={() => setConfirming(null)}>
                            Cancel
                          </Button>
                        </>
                      ) : (
                        <>
                          {isOwner && m.role === 'Staff' && (
                            <Button size="sm" variant="ghost" onClick={() => setConfirming({ userId: m.userId, kind: 'owner' })}>
                              Make owner
                            </Button>
                          )}
                          {action && (
                            <Button size="sm" variant="ghost" onClick={() => setConfirming(action)}>
                              {action.kind === 'leave' ? 'Leave' : 'Remove'}
                            </Button>
                          )}
                        </>
                      )}
                    </span>
                  </li>
                )
              })}
            </ul>
          )}
          {members.error && (
            <Alert variant="destructive">
              <AlertDescription>{members.error.message}</AlertDescription>
            </Alert>
          )}
          {confirming?.kind === 'owner' && (
            <Alert>
              <AlertDescription>
                The new owner runs the workspace and you become staff. Your Stripe account is disconnected from it: sales pause until the new
                owner sets up payouts with their own account. Payouts for earlier sales still go to you.
              </AlertDescription>
            </Alert>
          )}
          {change.error && (
            <Alert variant="destructive">
              <AlertDescription>{change.error.message}</AlertDescription>
            </Alert>
          )}
          {isOwner && (
              <form onSubmit={onInvite} className="flex flex-wrap items-end gap-3">
              <Field id="invite-email" label="Invite staff by email" hint="They get an email with a link, valid for 7 days, and join as staff." className="min-w-64 flex-1">
                <Input id="invite-email" name="email" type="email" required maxLength={320} />
              </Field>
              <Button type="submit" disabled={invite.isPending} className="mb-6">
                Send invite
              </Button>
            </form>
          )}
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
