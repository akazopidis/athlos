import { useMutation, useQuery } from '@tanstack/react-query'
import { Check, ChevronRight, CirclePlay, Lock, RotateCcw, ShoppingBag } from 'lucide-react'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { BrandScope } from '@/components/brand'
import { Loading, Notice, Section } from '@/components/page'
import type { components } from '@/api/catalog'
import { CoachAvatar, CoachLinks } from '@/components/coach-profile'
import { DiscountNote } from '@/components/discount-note'
import { ProgramCover } from '@/components/program-cover'
import { SaveButton } from '@/components/save-button'
import { Stars } from '@/components/stars'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { formatRating } from '@/lib/rating'
import { MaxCartItems, useCart } from '@/lib/cart'
import { categoryLabel } from '@/lib/categories'
import { TermsVersion } from '@/lib/legal'
import { formatMoney } from '@/lib/money'
import { leaveTo } from '@/lib/navigation'
import { problemTitle } from '@/lib/problem'
import { useCoaches } from '@/lib/useCoaches'
import { useMyWorkspaces, useMyWorkspacesPending } from '@/lib/useMyWorkspaces'
import { useOwnedPrograms, useOwnedProgramsPending } from '@/lib/useOwnedPrograms'
import { useSlow } from '@/lib/useSlow'
import { useTitle } from '@/lib/useTitle'
import { ProductCard } from '@/components/product-card'
import { ProgramReviews } from './ProgramReviews.tsx'
import { useStorefront } from './useStorefront.ts'

// A published program's product page, in the coach's colors: breadcrumbs, the buy box ("Add to cart",
// or "Buy now", a checkout of this program alone), what is included, reviews, and more from the same coach.
// Checkout sends the customer to Stripe; Stripe returns them to /me (paid) or to /cart (cancelled).
export function ProgramPage() {
  const { slug = '', programSlug = '' } = useParams()
  const { api, auth, config } = useApp()
  const signedIn = Boolean(auth?.account)
  const coach = useStorefront(slug)
  const cart = useCart()

  // One key per visit: a double click or a retry reuses the same checkout page (design §7.1).
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
  const owned = useOwnedPrograms()
  const ownsIt = Boolean(program.data && owned.has(program.data.id))
  // A program of a workspace the visitor coaches in: theirs in the studio, not for sale to them.
  const mine = useMyWorkspaces().has(slug)
  // Until both are known, the buy box shows neither "Buy" nor "Open": no flicker for coaches and owners.
  const workspacesPending = useMyWorkspacesPending()
  const ownedPending = useOwnedProgramsPending()
  const deciding = workspacesPending || ownedPending
  useTitle(program.data?.title)

  const buy = useMutation({
    mutationFn: async (programId: string) => {
      const { data, error, response } = await api.POST('/api/v1/orders/checkout', {
        body: { programIds: [programId], termsVersion: TermsVersion },
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
  const slow = useSlow(buy.isPending)

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
  const inCart = cart.has(p.id)
  const cartFull = cart.items.length >= MaxCartItems
  const included = [
    `${weeks} ${weeks === 1 ? 'week' : 'weeks'} of structured training`,
    'Every workout with instructions, and videos where the coach adds them',
    'Your progress saved as you tick off workouts',
    'One payment, no subscription',
  ]

  const addToCart = () =>
    cart.add({
      programId: p.id,
      coachSlug: p.coachSlug,
      slug: p.slug,
      title: p.title,
      priceAmount: Number(p.priceAmount),
      currency: p.currency,
      coverImageUrl: p.coverImageUrl,
      category: p.category,
    })

  return (
    <BrandScope color={coach.data?.branding?.primaryColor}>
      <article className="space-y-6">
        <nav aria-label="Breadcrumb">
          <ol className="flex flex-wrap items-center gap-1 text-sm text-muted-foreground">
            <li>
              <Link to="/" className="hover:text-foreground hover:underline">
                Programs
              </Link>
            </li>
            <li aria-hidden>
              <ChevronRight className="size-3.5" />
            </li>
            <li>
              <Link to={`/c/${p.coachSlug}`} className="hover:text-foreground hover:underline">
                {coachName}
              </Link>
            </li>
            <li aria-hidden>
              <ChevronRight className="size-3.5" />
            </li>
            <li aria-current="page" className="max-w-[60vw] truncate font-medium text-foreground">
              {p.title}
            </li>
          </ol>
        </nav>
        <div className="grid gap-8 lg:grid-cols-[minmax(0,1fr)_22rem] lg:gap-12">
          <div className="min-w-0 space-y-8">
            <ProgramCover url={p.coverImageUrl} title={p.title} category={p.category} className="aspect-video rounded-3xl" />
            <div className="space-y-4">
              <div className="flex flex-wrap items-center gap-2">
                <Badge variant="secondary">{categoryLabel(p.category)}</Badge>
                <Badge variant="outline">
                  {weeks} {weeks === 1 ? 'week' : 'weeks'}
                </Badge>
                {ownsIt && <Badge>Owned</Badge>}
                {!ownsIt && !mine && <SaveButton programId={p.id} title={p.title} className="ml-auto" />}
              </div>
              <h1 className="text-4xl leading-tight font-black tracking-tight sm:text-5xl">{p.title}</h1>
              <p className="text-muted-foreground">
                by{' '}
                <Link to={`/c/${p.coachSlug}`} className="font-medium text-foreground underline-offset-4 hover:underline">
                  {coachName}
                </Link>
              </p>
              {Number(p.ratingCount ?? 0) > 0 && (
                <a href="#reviews" className="inline-flex items-center gap-2 text-sm underline-offset-4 hover:underline">
                  <Stars value={Number(p.ratingAverage)} />
                  <span className="font-semibold">{formatRating(Number(p.ratingAverage))}</span>
                  <span className="text-muted-foreground">
                    {Number(p.ratingCount)} {Number(p.ratingCount) === 1 ? 'review' : 'reviews'}
                  </span>
                </a>
              )}
              {p.description && <p className="max-w-prose text-lg whitespace-pre-line text-muted-foreground">{p.description}</p>}
            </div>
            <ProgramOutline outline={p.outline} weeks={weeks} />
            {p.sample && <FreePreview sample={p.sample} />}
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
            <ProgramReviews programId={p.id} coachName={coachName} />
            {coach.data && (
              <section aria-labelledby="coach-heading" className="flex flex-col gap-5 rounded-3xl border bg-card p-6 sm:flex-row sm:p-8">
                <CoachAvatar name={coachName} profile={coach.data.branding} className="size-16 text-2xl" />
                <div className="min-w-0 flex-1 space-y-3">
                  <p className="text-xs font-semibold tracking-[0.15em] text-muted-foreground uppercase">Your coach</p>
                  <h2 id="coach-heading" className="text-xl font-bold">
                    <Link to={`/c/${p.coachSlug}`} className="underline-offset-4 hover:underline">
                      {coachName}
                    </Link>
                  </h2>
                  {coach.data.branding.bio && <p className="line-clamp-4 whitespace-pre-line text-sm text-muted-foreground">{coach.data.branding.bio}</p>}
                  <CoachLinks name={coachName} profile={coach.data.branding} />
                </div>
              </section>
            )}
          </div>

          {/* The buy box: a bar pinned to the bottom of the screen on phones, a card that follows the
              scroll beside the content on large screens. */}
          <aside className="sticky bottom-0 z-30 -mx-4 border-t bg-background/95 p-4 backdrop-blur sm:-mx-6 sm:px-6 lg:top-24 lg:bottom-auto lg:mx-0 lg:self-start lg:rounded-3xl lg:border lg:bg-card lg:p-6 lg:shadow-sm">
            <div className="flex flex-wrap items-center gap-3 lg:flex-col lg:items-stretch">
              <div className="min-w-0 lg:space-y-1">
                <p className="font-heading text-2xl font-black tabular-nums lg:text-4xl">{formatMoney(p.priceAmount, p.currency)}</p>
                {p.discount && !ownsIt && !mine && <DiscountNote offer={p.discount} price={Number(p.priceAmount)} currency={p.currency} />}
                <p className="hidden text-sm text-muted-foreground lg:block">One payment for every workout. No subscription.</p>
              </div>
              {deciding ? (
                <div className="h-10 flex-1 animate-pulse rounded-lg bg-muted lg:h-[5.5rem] lg:flex-none" aria-busy="true" aria-label="Loading" />
              ) : mine ? (
                <div className="flex-1 space-y-2 lg:flex-none">
                  <p className="hidden text-sm font-medium lg:block">This is your workspace's program.</p>
                  <Button asChild className="w-full" size="lg" variant="outline">
                    <Link to={`/studio/${p.coachSlug}`}>Open in studio</Link>
                  </Button>
                </div>
              ) : ownsIt ? (
                <div className="flex-1 space-y-2 lg:flex-none">
                  <p className="hidden text-sm font-medium lg:block">You own this program.</p>
                  <Button asChild className="w-full" size="lg">
                    <Link to={`/me/programs/${p.id}`}>Open program</Link>
                  </Button>
                </div>
              ) : (
                <div className="flex flex-1 gap-2 lg:flex-none lg:flex-col">
                  {inCart ? (
                    <Button asChild className="flex-1" size="lg" variant="secondary">
                      <Link to="/cart">
                        <Check aria-hidden /> In cart
                      </Link>
                    </Button>
                  ) : (
                    <Button className="flex-1" size="lg" onClick={addToCart} disabled={cartFull} title={cartFull ? `A cart holds up to ${MaxCartItems} programs` : undefined}>
                      <ShoppingBag aria-hidden /> Add to cart
                    </Button>
                  )}
                  {signedIn ? (
                    <Button className="flex-1" size="lg" variant="outline" onClick={() => buy.mutate(p.id)} disabled={buy.isPending || buy.isSuccess}>
                      {buy.isPending || buy.isSuccess ? 'Opening checkout…' : 'Buy now'}
                    </Button>
                  ) : auth ? (
                    <Button className="flex-1" size="lg" variant="outline" onClick={() => auth.signIn()}>
                      Sign in to buy
                    </Button>
                  ) : null}
                </div>
              )}
            </div>
            {signedIn && !deciding && !mine && !ownsIt && (
              <p className="mt-3 text-center text-xs text-muted-foreground">
                By buying you accept the{' '}
                <Link to="/legal/terms" className="underline underline-offset-4">
                  Terms
                </Link>{' '}
                and the{' '}
                <Link to="/legal/refunds" className="underline underline-offset-4">
                  Refund policy
                </Link>
                .
              </p>
            )}
            {slow && <div className="mt-3"><p className="text-center text-xs text-muted-foreground" role="status">Waking up the payment service. This can take up to a minute the first time.</p></div>}
            {buy.error && (
              <Alert variant="destructive" role="alert" className="mt-3">
                <AlertDescription>{buy.error.message}</AlertDescription>
              </Alert>
            )}
            <ul className="mt-4 space-y-1.5 text-xs text-muted-foreground">
              <li className="flex items-center justify-center gap-1.5">
                <RotateCcw className="size-3.5" aria-hidden /> Refundable by the coach within {config.refundWindowDays ?? 14} days
              </li>
              <li className="hidden items-center justify-center gap-1.5 lg:flex">
                <Lock className="size-3.5" aria-hidden /> Secure checkout with Stripe
              </li>
            </ul>
          </aside>
        </div>
        <MoreFromCoach coachSlug={p.coachSlug} coachName={coachName} exclude={p.id} />
      </article>
    </BrandScope>
  )
}

// Up to four other programs by the same coach, as product cards.
function MoreFromCoach({ coachSlug, coachName, exclude }: { coachSlug: string; coachName: string; exclude: string }) {
  const { api } = useApp()
  const owned = useOwnedPrograms()
  const mine = useMyWorkspaces()
  const coaches = useCoaches([coachSlug])
  const more = useQuery({
    queryKey: ['programs', coachSlug, 'more'],
    queryFn: async () => (await api.GET('/api/v1/marketplace/programs', { params: { query: { Coach: coachSlug, PageSize: 5 } } })).data ?? null,
  })
  const items = (more.data?.items ?? []).filter((i) => i.id !== exclude).slice(0, 4)
  if (!items.length) return null

  return (
    <Section title={`More from ${coachName}`} className="pt-10">
      <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {items.map((i) => (
          <li key={i.id}>
            <ProductCard program={i} coach={coaches.get(i.coachSlug)} owned={owned.has(i.id)} mine={mine.has(i.coachSlug)} />
          </li>
        ))}
      </ul>
    </Section>
  )
}

type Program = components['schemas']['GetPublishedProgramResponse']

// "What's inside": every workout's week, day and title, grouped by week (the paid content stays hidden).
function ProgramOutline({ outline, weeks }: { outline: Program['outline']; weeks: number }) {
  // Absent from a catalog-api older than the outline (a rolling deploy): nothing to show.
  if (!outline?.length) return null
  const byWeek = new Map<number, Program['outline']>()
  for (const o of outline) byWeek.set(Number(o.week), [...(byWeek.get(Number(o.week)) ?? []), o])

  return (
    <section aria-labelledby="outline-heading" className="space-y-4 rounded-3xl border bg-card p-6 sm:p-8">
      <div>
        <h2 id="outline-heading" className="text-xl font-bold">
          What's inside
        </h2>
        <p className="text-sm text-muted-foreground">
          {outline.length} {outline.length === 1 ? 'workout' : 'workouts'} over {weeks} {weeks === 1 ? 'week' : 'weeks'}
        </p>
      </div>
      <ol className="space-y-4">
        {[...byWeek].map(([week, items]) => (
          <li key={week} className="space-y-2">
            <p className="text-xs font-semibold tracking-[0.15em] text-muted-foreground uppercase">Week {week}</p>
            <ul className="divide-y rounded-2xl border">
              {items.map((o) => (
                <li key={`${o.week}-${o.day}-${o.title}`} className="flex items-center gap-3 px-4 py-2.5 text-sm">
                  <span className="w-12 shrink-0 text-muted-foreground">Day {Number(o.day)}</span>
                  <span className="min-w-0 flex-1 truncate font-medium">{o.title}</span>
                  {o.sample ? (
                    <Badge variant="secondary">Free preview</Badge>
                  ) : (
                    <Lock className="size-3.5 shrink-0 text-muted-foreground" aria-label="Included when you buy" />
                  )}
                </li>
              ))}
            </ul>
          </li>
        ))}
      </ol>
    </section>
  )
}

// The one workout the coach shows in full, so customers can try before they buy.
function FreePreview({ sample }: { sample: NonNullable<Program['sample']> }) {
  return (
    <section aria-labelledby="preview-heading" className="space-y-3 rounded-3xl border border-primary/40 bg-primary/5 p-6 sm:p-8">
      <p className="flex items-center gap-2 text-xs font-semibold tracking-[0.15em] uppercase">
        <span aria-hidden className="size-2 rounded-full bg-primary" /> Free preview
      </p>
      <h2 id="preview-heading" className="text-xl font-bold">
        Week {Number(sample.week)}, day {Number(sample.day)}: {sample.title}
      </h2>
      {sample.content && <p className="whitespace-pre-line text-muted-foreground">{sample.content}</p>}
      {sample.videoUrl && (
        <Button asChild variant="outline" size="sm">
          <a href={sample.videoUrl} target="_blank" rel="noreferrer">
            <CirclePlay aria-hidden /> Watch the video
          </a>
        </Button>
      )}
    </section>
  )
}
