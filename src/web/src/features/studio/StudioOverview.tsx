import { useMutation, useQuery } from '@tanstack/react-query'
import { Circle, CircleCheck, Wallet } from 'lucide-react'
import { CopyLink } from '@/components/copy-link'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Section } from '@/components/page'
import { Skeleton } from '@/components/ui/skeleton'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'
import { StudioPrograms } from './StudioPrograms.tsx'
import { useStudio } from './useStudio.ts'

// The studio's first tab: a setup checklist until the first program is on sale, the storefront's link
// to share, payout setup (needed before publishing, design §3.4) and the programs.
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

  // The same query as the program list below (shared cache).
  const programs = useQuery({
    queryKey: ['studio-programs', slug],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/programs', params)).data ?? [],
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

  const steps = [
    { label: 'Set up payouts with Stripe', done: Boolean(payouts.data?.onboardingComplete) },
    { label: 'Create a program and add its workouts', done: Boolean(programs.data?.length) },
    { label: 'Publish it', done: Boolean(programs.data?.some((p) => p.isPublished)) },
  ]
  const known = payouts.isSuccess && programs.isSuccess

  return (
    <div className="space-y-12">
      {known && steps.some((s) => !s.done) && (
        <Section title="Get started">
          <ol className="grid gap-3 sm:grid-cols-3">
            {steps.map((s, i) => (
              <li key={s.label} className="flex items-center gap-3 rounded-2xl border bg-card p-4">
                {s.done ? <CircleCheck className="size-5 shrink-0 text-primary" aria-hidden /> : <Circle className="size-5 shrink-0 text-muted-foreground" aria-hidden />}
                <span className={s.done ? 'text-muted-foreground line-through' : 'font-medium'}>
                  {i + 1}. {s.label}
                </span>
                <span className="sr-only">{s.done ? '(done)' : '(to do)'}</span>
              </li>
            ))}
          </ol>
        </Section>
      )}
      <Section title="Your storefront">
        <p className="text-sm text-muted-foreground">Share this link on your website and social media: customers see your published programs in your colors.</p>
        <CopyLink path={`/c/${slug}`} />
      </Section>
      <Section title="Payouts">
        {/* Nothing until the status is known: a call to action that then disappears would mislead. */}
        {payouts.isPending ? (
          <Skeleton className="h-20 w-full rounded-xl" />
        ) : payouts.data?.onboardingComplete ? (
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
      <StudioPrograms slug={slug} plan={workspace.data?.plan} />
    </div>
  )
}
