import { useMutation, useQuery } from '@tanstack/react-query'
import { CircleCheck } from 'lucide-react'
import { Link, useParams } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Loading, PageHeader, Section } from '@/components/page'
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

  if (workspace.isPending) return <Loading lines={4} />
  if (!workspace.data) return <p>Workspace not found.</p>

  const { displayName, plan, status } = workspace.data
  return (
    <div className="space-y-12">
      <PageHeader title={displayName} description="Studio">
        <div className="flex gap-2">
          <Badge variant="outline">{plan} plan</Badge>
          <Badge variant={status === 'Active' ? 'secondary' : 'destructive'}>{status}</Badge>
          <Button asChild variant="outline" size="sm">
            <Link to={`/c/${slug}`}>View storefront</Link>
          </Button>
        </div>
      </PageHeader>
      <Section title="Payouts">
        {payouts.data?.onboardingComplete ? (
          <Alert>
            <CircleCheck aria-hidden />
            <AlertDescription>Payouts are set up. You can publish programs.</AlertDescription>
          </Alert>
        ) : (
          <Card>
            <CardContent className="flex flex-wrap items-center justify-between gap-4">
              <p className="text-sm text-muted-foreground">Set up payouts with Stripe before publishing programs.</p>
              <Button onClick={() => onboarding.mutate()} disabled={onboarding.isPending}>
                {payouts.data?.connected ? 'Continue payout setup' : 'Set up payouts'}
              </Button>
            </CardContent>
          </Card>
        )}
        {onboarding.error && (
          <Alert variant="destructive">
            <AlertDescription>{onboarding.error.message}</AlertDescription>
          </Alert>
        )}
      </Section>
      <StudioPrograms slug={slug} />
      {isOwner && <StudioTeam slug={slug} />}
    </div>
  )
}
