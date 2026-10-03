import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { FormEvent } from 'react'
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
    <section>
      <h2>Team</h2>
      <form onSubmit={onInvite}>
        <label>
          Invite staff by email
          <input name="email" type="email" required maxLength={320} />
          <span className="hint">They get an email with a link, valid for 7 days, and join as staff.</span>
        </label>
        <button type="submit" disabled={invite.isPending}>
          Send invite
        </button>
        {invite.isSuccess && <p role="status">Invite sent.</p>}
        {invite.error && <p role="alert">{invite.error.message}</p>}
      </form>
      {invites.data && invites.data.length > 0 && (
        <>
          <h3>Open invites</h3>
          <ul>
            {invites.data.map((i) => (
              <li key={i.id}>
                {i.email} · {i.expired ? 'expired' : `valid until ${new Date(i.expiresAt).toLocaleDateString()}`}{' '}
                <button type="button" onClick={() => revoke.mutate(i.id)} disabled={revoke.isPending}>
                  Revoke
                </button>
              </li>
            ))}
          </ul>
          {revoke.error && <p role="alert">{revoke.error.message}</p>}
        </>
      )}
    </section>
  )
}
