import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router'
import { useApp } from '../AppContext.tsx'

// Coach dashboard. The gateway checks membership; another tenant's workspace is a plain 404.
export function Studio() {
  const { slug = '' } = useParams()
  const { api } = useApp()

  const workspace = useQuery({
    queryKey: ['workspace', slug],
    queryFn: async () => {
      const { data } = await api.GET('/api/v1/studio/{tenantSlug}/workspace', { params: { path: { tenantSlug: slug } } })
      return data ?? null
    },
  })

  if (workspace.isPending) return <p>Loading…</p>
  if (!workspace.data) return <p>Workspace not found.</p>

  const { displayName, plan, status, stripeOnboardingComplete } = workspace.data
  return (
    <section>
      <h1>{displayName}</h1>
      <p>
        Plan: {plan} · Status: {status}
      </p>
      {!stripeOnboardingComplete && <p>Connect Stripe to publish programs (phase 4).</p>}
    </section>
  )
}
