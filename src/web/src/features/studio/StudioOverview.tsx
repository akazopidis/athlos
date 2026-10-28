import { useMutation, useQuery } from '@tanstack/react-query'
import { Circle, CircleCheck, Wallet } from 'lucide-react'
import { Link } from 'react-router'
import type { components } from '@/api/ordering'
import { CopyLink } from '@/components/copy-link'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Section } from '@/components/page'
import { Skeleton } from '@/components/ui/skeleton'
import { Stars } from '@/components/stars'
import { useApp } from '@/lib/AppContext'
import { formatMoney } from '@/lib/money'
import { problemTitle } from '@/lib/problem'
import { formatRating } from '@/lib/rating'
import { StudioPayouts } from './StudioPayouts.tsx'
import { StudioPrograms } from './StudioPrograms.tsx'
import { useStudio } from './useStudio.ts'

// The studio's first tab: a setup checklist until the first program is on sale, the storefront's link
// to share, the numbers and the latest reviews once there are some, payout setup (needed before
// publishing, design §3.4) and the programs.
export function StudioOverview() {
  const { slug, workspace, isOwner } = useStudio()
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

  // The numbers (ordering-api): paid sales and earnings after the fee.
  const summary = useQuery({
    queryKey: ['sales-summary', slug],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/sales/summary', params)).data ?? null,
    enabled: Boolean(workspace.data),
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
      {summary.data && Number(summary.data.sales) > 0 && <Numbers summary={summary.data} />}
      <LatestReviews slug={slug} />
      <Section title="Your storefront">
        <p className="text-sm text-muted-foreground">Share this link on your website and social media: customers see your published programs in your colors.</p>
        <CopyLink path={`/c/${slug}`} />
      </Section>
      <Section title="Payouts">
        {/* Nothing until the status is known: a call to action that then disappears would mislead. */}
        {payouts.isPending ? (
          <Skeleton className="h-20 w-full rounded-xl" />
        ) : payouts.data?.onboardingComplete ? (
          <StudioPayouts slug={slug} isOwner={isOwner} />
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

// The workspace's rating and its two newest reviews (enrollment-api, design §2.4), once there are any.
function LatestReviews({ slug }: { slug: string }) {
  const { api } = useApp()
  const reviews = useQuery({
    queryKey: ['studio-reviews', slug, 'latest'],
    queryFn: async () =>
      (await api.GET('/api/v1/studio/{tenantSlug}/reviews', { params: { path: { tenantSlug: slug }, query: { Page: 1, PageSize: 2 } } })).data ?? null,
  })
  const data = reviews.data
  if (!data || Number(data.reviewCount) === 0) return null

  const count = Number(data.reviewCount)
  return (
    <Section
      title="Your reviews"
      action={
        <Button asChild variant="outline" size="sm">
          <Link to={`/studio/${slug}/reviews`}>All reviews</Link>
        </Button>
      }
    >
      <div className="flex items-center gap-3">
        <span className="font-heading text-3xl font-black tabular-nums">{formatRating(Number(data.average))}</span>
        <Stars value={Number(data.average)} />
        <span className="text-sm text-muted-foreground">
          {count} {count === 1 ? 'review' : 'reviews'}
        </span>
      </div>
      <ul className="grid gap-3 sm:grid-cols-2">
        {data.items.map((r) => (
          <li key={r.id} className="space-y-1 rounded-2xl border bg-card p-4">
            <div className="flex items-center gap-2">
              <Stars value={Number(r.rating)} className="size-3.5" />
              <span className="text-sm font-semibold">{r.authorName}</span>
              <span className="ml-auto truncate text-xs text-muted-foreground">{r.programTitle}</span>
            </div>
            <p className="line-clamp-2 text-sm text-muted-foreground">{r.comment ?? 'No comment, just stars.'}</p>
            {!r.reply && <p className="text-xs font-medium">Not answered yet</p>}
          </li>
        ))}
      </ul>
    </Section>
  )
}

type Summary = components['schemas']['SalesSummaryResponse']

// This month's sales and earnings, the total, customers and the best seller, once there are sales.
function Numbers({ summary }: { summary: Summary }) {
  const tiles = [
    { label: 'Sales this month', value: String(Number(summary.salesThisMonth)) },
    { label: 'Earned this month', value: formatMoney(summary.earningsThisMonth, summary.currency) },
    { label: 'Earned in total', value: formatMoney(summary.earnings, summary.currency) },
    { label: 'Customers', value: String(Number(summary.customers)) },
  ]
  return (
    <Section title="Your numbers">
      <dl className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        {tiles.map((t) => (
          <div key={t.label} className="rounded-2xl border bg-card p-4">
            <dt className="text-sm text-muted-foreground">{t.label}</dt>
            <dd className="mt-1 font-heading text-2xl font-black tabular-nums">{t.value}</dd>
          </div>
        ))}
      </dl>
      {summary.bestSeller && (
        <p className="text-sm text-muted-foreground">
          Best seller: <span className="font-medium text-foreground">{summary.bestSeller}</span> ({Number(summary.bestSellerSales)}{' '}
          {Number(summary.bestSellerSales) === 1 ? 'sale' : 'sales'}). Earnings are after the platform fee.
        </p>
      )}
    </Section>
  )
}
