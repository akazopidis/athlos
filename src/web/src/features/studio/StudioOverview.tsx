import { useMutation, useQuery } from '@tanstack/react-query'
import { CircleCheck, Wallet } from 'lucide-react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Section } from '@/components/page'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'
import { StudioPrograms } from './StudioPrograms.tsx'
import { useStudio } from './useStudio.ts'

// The studio's first tab: payout setup (needed before publishing, design §3.4) and the programs.
export function StudioOverview() {
  const { slug, workspace } = useStudio()
  const { api } = useApp()
  const params = { params: { path: { tenantSlug: slug } } }

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

  return (
    <div className="space-y-12">
      <Section title="Payouts">
        {payouts.data?.onboardingComplete ? (
          <Alert>
            <CircleCheck aria-hidden />
            <AlertDescription>Payouts are set up. You can publish programs.</AlertDescription>
          </Alert>
        ) : (
          <Card className="border-volt bg-volt/15">
            <CardContent className="flex flex-wrap items-center gap-4">
              <span className="flex size-11 shrink-0 items-center justify-center rounded-2xl bg-volt text-volt-foreground">
                <Wallet className="size-5" aria-hidden />
              </span>
              <div className="min-w-0 flex-1 basis-56">
                <p className="font-semibold">Get paid for your programs</p>
                <p className="text-sm text-muted-foreground">Set up payouts with Stripe before publishing programs. Payments go straight to your account.</p>
              </div>
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
    </div>
  )
}
