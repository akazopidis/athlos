import { useMutation, useQuery } from '@tanstack/react-query'
import { ArrowLeft, Check, Lock } from 'lucide-react'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { BrandScope } from '@/components/brand'
import { Loading, Notice } from '@/components/page'
import { ProgramCover } from '@/components/program-cover'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { formatMoney } from '@/lib/money'
import { leaveTo } from '@/lib/navigation'
import { problemTitle } from '@/lib/problem'
import { useTitle } from '@/lib/useTitle'
import { useStorefront } from './useStorefront.ts'

// A published program's public page with its Buy button, in the coach's colors. Buying sends the
// customer to Stripe Checkout; Stripe returns them to /me (paid) or to the storefront (cancelled).
export function ProgramPage() {
  const { slug = '', programSlug = '' } = useParams()
  const { api, msal, config } = useApp()
  const signedIn = Boolean(msal?.getActiveAccount())
  const coach = useStorefront(slug)

  // One key per visit: a double click or a retry reuses the same order and checkout page (design §7.1).
  const [idempotencyKey] = useState(() => crypto.randomUUID())

  const program = useQuery({
    queryKey: ['program', slug, programSlug],
    queryFn: async () =>
      (
        await api.GET('/api/v1/marketplace/coaches/{coachSlug}/programs/{programSlug}', {
          params: { path: { coachSlug: slug, programSlug } },
        })
      ).data ?? null,
  })

  // A customer who already bought it gets the way in, not a second checkout (which would be a 409).
  const owned = useQuery({
    queryKey: ['my-programs'],
    queryFn: async () => (await api.GET('/api/v1/me/programs')).data ?? null,
    enabled: signedIn,
  })
  const ownsIt = Boolean(program.data && owned.data?.items.some((o) => o.programId === program.data?.id))
  useTitle(program.data?.title)

  const buy = useMutation({
    mutationFn: async (programId: string) => {
      const { data, error, response } = await api.POST('/api/v1/orders/checkout', {
        body: { programId },
        params: { header: { 'Idempotency-Key': idempotencyKey } },
      })
      if (data) return data
      if (response.status === 401) throw new Error('Sign in to buy this program.')
      if (response.status === 404) throw new Error('This program is no longer for sale.')
      if (response.status === 503) throw new Error('Payments are not available yet in this environment.')
      throw new Error(problemTitle(error) ?? 'Could not start checkout.')
    },
    onSuccess: ({ checkoutUrl }) => leaveTo(checkoutUrl),
  })

  if (program.isPending) return <Loading lines={5} />
  if (!program.data) {
    return (
      <Notice
        title="Program not found"
        action={
          <Button asChild>
            <Link to="/">Browse programs</Link>
          </Button>
        }
      >
        It may have been taken off sale.
      </Notice>
    )
  }

  const p = program.data
  const coachName = coach.data?.displayName ?? p.coachSlug
  const weeks = Number(p.durationWeeks)
  const included = [
    `${weeks} ${weeks === 1 ? 'week' : 'weeks'} of structured training`,
    'Every workout with instructions, and videos where the coach adds them',
    'Your progress saved as you tick off workouts',
    'One payment, yours to keep',
  ]

  return (
    <BrandScope color={coach.data?.branding?.primaryColor}>
      <article className="space-y-6">
        <Button asChild variant="ghost" size="sm" className="-ml-3">
          <Link to={`/c/${p.coachSlug}`}>
            <ArrowLeft aria-hidden /> {coachName}
          </Link>
        </Button>
        <div className="grid gap-8 lg:grid-cols-[minmax(0,1fr)_22rem] lg:gap-12">
          <div className="min-w-0 space-y-8">
            <ProgramCover url={p.coverImageUrl} title={p.title} className="aspect-video rounded-3xl" />
            <div className="space-y-4">
              <div className="flex flex-wrap gap-2">
                <Badge variant="secondary">{p.level}</Badge>
                <Badge variant="outline">
                  {weeks} {weeks === 1 ? 'week' : 'weeks'}
                </Badge>
              </div>
              <h1 className="text-4xl leading-tight font-black tracking-tight sm:text-5xl">{p.title}</h1>
              <p className="text-muted-foreground">
                by{' '}
                <Link to={`/c/${p.coachSlug}`} className="font-medium text-foreground underline-offset-4 hover:underline">
                  {coachName}
                </Link>
              </p>
              {p.description && <p className="max-w-prose text-lg whitespace-pre-line text-muted-foreground">{p.description}</p>}
            </div>
            <section className="space-y-4 rounded-3xl border bg-card p-6 sm:p-8">
              <h2 className="text-xl font-bold">What you get</h2>
              <ul className="grid gap-3 sm:grid-cols-2">
                {included.map((item) => (
                  <li key={item} className="flex gap-3 text-sm">
                    <span className="mt-0.5 flex size-5 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground">
                      <Check className="size-3.5" aria-hidden />
                    </span>
                    {item}
                  </li>
                ))}
              </ul>
            </section>
          </div>

          {/* One purchase panel: a bar pinned to the bottom of the screen on phones, a card that
              follows the scroll beside the content on large screens. */}
          <aside className="sticky bottom-0 z-30 -mx-4 border-t bg-background/95 p-4 backdrop-blur sm:-mx-6 sm:px-6 lg:top-24 lg:bottom-auto lg:mx-0 lg:self-start lg:rounded-3xl lg:border lg:bg-card lg:p-6 lg:shadow-sm">
            <div className="flex items-center gap-4 lg:flex-col lg:items-stretch">
              <div className="min-w-0 lg:space-y-1">
                <p className="font-heading text-2xl font-black tabular-nums lg:text-4xl">{formatMoney(p.priceAmount, p.currency)}</p>
                <p className="hidden text-sm text-muted-foreground lg:block">One payment. All workouts, yours to keep.</p>
              </div>
              <div className="flex-1 space-y-2 lg:flex-none">
                {ownsIt ? (
                  <>
                    <p className="hidden text-sm font-medium lg:block">You own this program.</p>
                    <Button asChild className="w-full" size="lg">
                      <Link to={`/me/programs/${p.id}`}>Open program</Link>
                    </Button>
                  </>
                ) : signedIn ? (
                  <Button className="w-full" size="lg" onClick={() => buy.mutate(p.id)} disabled={buy.isPending || buy.isSuccess}>
                    {buy.isPending || buy.isSuccess ? 'Opening checkout…' : 'Buy'}
                  </Button>
                ) : msal ? (
                  <Button className="w-full" size="lg" onClick={() => msal.loginRedirect({ scopes: [config.apiScope!] })}>
                    Sign in to buy
                  </Button>
                ) : (
                  <p className="text-sm">Sign-in is not available in this environment.</p>
                )}
              </div>
            </div>
            {buy.error && (
              <Alert variant="destructive" role="alert" className="mt-3">
                <AlertDescription>{buy.error.message}</AlertDescription>
              </Alert>
            )}
            <p className="mt-4 hidden items-center justify-center gap-1.5 text-xs text-muted-foreground lg:flex">
              <Lock className="size-3.5" aria-hidden /> Secure checkout with Stripe
            </p>
          </aside>
        </div>
      </article>
    </BrandScope>
  )
}
