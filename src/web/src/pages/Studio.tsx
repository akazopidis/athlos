import { useMutation, useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router'
import { useApp } from '../AppContext.tsx'
import { problemTitle } from '../problem.ts'
import { StudioPrograms } from './StudioPrograms.tsx'
import { StudioTeam } from './StudioTeam.tsx'

// Coach dashboard. The gateway checks membership; another tenant's workspace is a plain 404.
export function Studio() {
  const { slug = '' } = useParams()
  const { api } = useApp()
  const params = { params: { path: { tenantSlug: slug } } }

  const workspace = useQuery({
    queryKey: ['workspace', slug],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/workspace', params)).data ?? null,
  })

  // The caller's role here: the team is managed by owners only.
  const memberships = useQuery({
    queryKey: ['memberships'],
    queryFn: async () => (await api.GET('/api/v1/me/memberships')).data ?? [],
  })
  const isOwner = memberships.data?.some((m) => m.slug === slug && m.role === 'Owner') ?? false

  // Read on every visit, including the return from Stripe's onboarding pages (?payouts=return).
  const payouts = useQuery({
    queryKey: ['payouts', slug],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/payouts', params)).data ?? null,
    enabled: Boolean(workspace.data),
  })

  const onboarding = useMutation({
    mutationFn: async () => {
      const { data, error, response } = await api.POST('/api/v1/studio/{tenantSlug}/payouts/onboarding', params)
      if (response.status === 403) throw new Error('Only the owner can set up payouts.')
      if (response.status === 503) throw new Error('Payments are not available yet in this environment.')
      if (!data) throw new Error(problemTitle(error) ?? 'Could not start payout setup.')
      return data
    },
    onSuccess: ({ url }) => window.location.assign(url),
  })

  if (workspace.isPending) return <p>Loading…</p>
  if (!workspace.data) return <p>Workspace not found.</p>

  const { displayName, plan, status } = workspace.data
  return (
    <section>
      <h1>{displayName}</h1>
      <p>
        Plan: {plan} · Status: {status}
      </p>
      <h2>Payouts</h2>
      {payouts.data?.onboardingComplete ? (
        <p>Payouts are set up. You can publish programs.</p>
      ) : (
        <>
          <p>Set up payouts with Stripe before publishing programs.</p>
          <button type="button" onClick={() => onboarding.mutate()} disabled={onboarding.isPending}>
            {payouts.data?.connected ? 'Continue payout setup' : 'Set up payouts'}
          </button>
          {onboarding.error && <p role="alert">{onboarding.error.message}</p>}
        </>
      )}
      <StudioPrograms slug={slug} />
      {isOwner && <StudioTeam slug={slug} />}
    </section>
  )
}
