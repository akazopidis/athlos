import { useMutation, useQueries } from '@tanstack/react-query'
import { Info, Lock, ShoppingBag, Trash2 } from 'lucide-react'
import { useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { Notice, PageHeader } from '@/components/page'
import { ProgramCover } from '@/components/program-cover'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { cartTotals, MaxCartItems, useCart, type CartItem } from '@/lib/cart'
import { formatMoney } from '@/lib/money'
import { leaveTo } from '@/lib/navigation'
import { problemTitle } from '@/lib/problem'
import { useCoaches } from '@/lib/useCoaches'
import { useOwnedPrograms } from '@/lib/useOwnedPrograms'
import { useSlow } from '@/lib/useSlow'
import { useTitle } from '@/lib/useTitle'

// The cart (design §2.2, §8): programs from any coaches, paid in one Stripe checkout. ordering-api
// checks every program again; what it refuses comes back as programIds, which this page removes
// (gone, already owned) or marks (the coach cannot take payments now). Prices are read again when the
// cart opens, so the total is what Stripe will charge, and a changed price is pointed out.
export function Cart() {
  useTitle('Cart')
  const { api, msal, config } = useApp()
  const cart = useCart()
  const owned = useOwnedPrograms()
  const coaches = useCoaches(cart.items.map((i) => i.coachSlug))
  const [params] = useSearchParams()
  const [notice, setNotice] = useState<string | null>(null)
  const [blocked, setBlocked] = useState<string[]>([])
  const signedIn = Boolean(msal?.getActiveAccount())

  // Each program's current price (its public page): the cart shows and totals what checkout will
  // charge, and names any price that changed since it was added. A program no longer for sale keeps
  // its stored price; checkout refuses it.
  const current = useQueries({
    queries: cart.items.map((i) => ({
      queryKey: ['cart-price', i.coachSlug, i.slug],
      queryFn: async () =>
        (await api.GET('/api/v1/marketplace/coaches/{coachSlug}/programs/{programSlug}', { params: { path: { coachSlug: i.coachSlug, programSlug: i.slug } } }))
          .data ?? null,
      staleTime: 60_000,
    })),
  })
  const priced = cart.items.map((item, n) => {
    const price = current[n]?.data?.priceAmount
    return typeof price === 'number' || typeof price === 'string' ? { ...item, priceAmount: Number(price) } : item
  })
  const changed = priced.filter((p, n) => Number(p.priceAmount) !== Number(cart.items[n]!.priceAmount))

  // One Idempotency-Key per cart content: a double click or a retry reuses the same checkout page,
  // a changed cart starts a new one (design §7.1).
  const attempt = useRef<{ ids: string; key: string } | null>(null)
  const keyFor = (ids: string[]) => {
    const joined = [...ids].sort().join(',')
    if (attempt.current?.ids !== joined) attempt.current = { ids: joined, key: crypto.randomUUID() }
    return attempt.current.key
  }

  const ownedInCart = priced.filter((i) => owned.has(i.programId))
  const toBuy = priced.filter((i) => !owned.has(i.programId))
  const totals = cartTotals(toBuy)

  const checkout = useMutation({
    mutationFn: async (programIds: string[]) => {
      setNotice(null)
      const { data, error, response } = await api.POST('/api/v1/orders/checkout', {
        body: { programIds },
        params: { header: { 'Idempotency-Key': keyFor(programIds) } },
      })
      if (data) return data
      const { reason, programIds: refused } = refusal(error)
      if (reason === 'notForSale' || reason === 'owned' || reason === 'ownWorkspace') {
        // Gone, already owned, or the buyer's own workspace's: out of the cart, and the customer is told which.
        const titles = cart.items.filter((i) => refused.includes(i.programId)).map((i) => i.title)
        cart.remove(...refused)
        setNotice(`${problemTitle(error) ?? 'Some programs cannot be bought.'} Removed from your cart: ${titles.join(', ')}.`)
        return null
      }
      if (reason === 'cannotSell') {
        // The cart stays; the coach's programs are marked (the notice below names the coach).
        setBlocked(refused)
        return null
      }
      if (response.status === 401) throw new Error('Sign in to check out.')
      if (response.status === 503) throw new Error('Payments are not available yet in this environment.')
      throw new Error(problemTitle(error) ?? 'Could not start checkout.')
    },
    onSuccess: (result) => result && leaveTo(result.checkoutUrl),
  })
  const slow = useSlow(checkout.isPending)
  const blockedItems = cart.items.filter((i) => blocked.includes(i.programId))

  if (!cart.items.length) {
    return (
      <div className="space-y-6">
        {params.get('checkout') === 'cancelled' && <Cancelled />}
        <Notice
          title="Your cart is empty"
          action={
            <Button asChild>
              <Link to="/">Shop programs</Link>
            </Button>
          }
        >
          Add programs from any coach and pay for them together.
        </Notice>
      </div>
    )
  }

  const byCoach = new Map<string, CartItem[]>()
  for (const item of priced) byCoach.set(item.coachSlug, [...(byCoach.get(item.coachSlug) ?? []), item])

  return (
    <div className="space-y-8">
      <PageHeader eyebrow="Cart" title="Your cart" description={`${cart.items.length} of up to ${MaxCartItems} programs, from ${byCoach.size} ${byCoach.size === 1 ? 'coach' : 'coaches'}.`} />
      {params.get('checkout') === 'cancelled' && <Cancelled />}
      {blockedItems.length > 0 && (
        <Alert role="status">
          <Info aria-hidden />
          <AlertDescription>
            {[...new Set(blockedItems.map((i) => coaches.get(i.coachSlug)?.displayName ?? i.coachSlug))].join(', ')} cannot take payments right now. Remove{' '}
            {blockedItems.length === 1 ? 'that program' : 'those programs'} to check out the rest.
          </AlertDescription>
        </Alert>
      )}
      {notice && (
        <Alert role="status">
          <Info aria-hidden />
          <AlertDescription>{notice}</AlertDescription>
        </Alert>
      )}
      {changed.length > 0 && (
        <Alert role="status">
          <Info aria-hidden />
          <AlertDescription>
            Prices changed since you added {changed.length === 1 ? 'this program' : 'these programs'}:{' '}
            {changed.map((i) => `${i.title} is now ${formatMoney(i.priceAmount, i.currency)}`).join(', ')}.
          </AlertDescription>
        </Alert>
      )}

      <div className="grid gap-8 lg:grid-cols-[minmax(0,1fr)_22rem] lg:items-start">
        <div className="space-y-6">
          {[...byCoach].map(([slug, items]) => (
            <section key={slug} className="rounded-3xl border bg-card" aria-label={`From ${coaches.get(slug)?.displayName ?? slug}`}>
              <h2 className="border-b px-5 py-3 text-sm font-semibold">
                From{' '}
                <Link to={`/c/${slug}`} className="underline-offset-4 hover:underline">
                  {coaches.get(slug)?.displayName ?? slug}
                </Link>
              </h2>
              <ul className="divide-y">
                {items.map((item) => (
                  <CartLine
                    key={item.programId}
                    item={item}
                    owned={owned.has(item.programId)}
                    blocked={blocked.includes(item.programId)}
                    onRemove={() => cart.remove(item.programId)}
                  />
                ))}
              </ul>
            </section>
          ))}
          <Button asChild variant="ghost">
            <Link to="/">Continue shopping</Link>
          </Button>
        </div>

        <aside className="space-y-4 rounded-3xl border bg-card p-6 shadow-sm lg:sticky lg:top-36">
          <h2 className="text-lg font-bold">Summary</h2>
          <dl className="space-y-2 text-sm">
            <div className="flex justify-between">
              <dt className="text-muted-foreground">Programs</dt>
              <dd className="tabular-nums">{toBuy.length}</dd>
            </div>
            {ownedInCart.length > 0 && (
              <div className="flex justify-between text-muted-foreground">
                <dt>Already yours, not charged</dt>
                <dd className="tabular-nums">{ownedInCart.length}</dd>
              </div>
            )}
            <div className="flex items-baseline justify-between border-t pt-3">
              <dt className="font-semibold">Total</dt>
              <dd className="font-heading text-2xl font-black tabular-nums">
                {totals.map(([currency, amount]) => formatMoney(amount, currency)).join(' + ') || '—'}
              </dd>
            </div>
          </dl>
          {totals.length > 1 && <p className="text-sm text-muted-foreground">Programs in different currencies are paid in separate checkouts.</p>}
          {checkout.error && (
            <Alert variant="destructive" role="alert">
              <AlertDescription>{checkout.error.message}</AlertDescription>
            </Alert>
          )}
          {signedIn ? (
            <Button
              className="w-full"
              size="lg"
              disabled={!toBuy.length || totals.length > 1 || checkout.isPending || Boolean(checkout.data)}
              onClick={() => checkout.mutate(toBuy.map((i) => i.programId))}
            >
              {checkout.isPending || checkout.data ? 'Opening checkout…' : 'Check out'}
            </Button>
          ) : msal ? (
            <Button className="w-full" size="lg" onClick={() => msal.loginRedirect({ scopes: [config.apiScope!] })}>
              Sign in to check out
            </Button>
          ) : (
            <p className="text-sm">Sign-in is not available in this environment.</p>
          )}
          {slow && <p className="text-center text-xs text-muted-foreground" role="status">Waking up the payment service. This can take up to a minute the first time.</p>}
          <p className="flex items-center justify-center gap-1.5 text-xs text-muted-foreground">
            <Lock className="size-3.5" aria-hidden /> One secure payment with Stripe for every coach
          </p>
        </aside>
      </div>
    </div>
  )
}

function CartLine({ item, owned, blocked, onRemove }: { item: CartItem; owned: boolean; blocked: boolean; onRemove: () => void }) {
  return (
    <li className="flex gap-4 p-4 sm:p-5">
      <Link to={`/c/${item.coachSlug}/programs/${item.slug}`} className="w-24 shrink-0 overflow-hidden rounded-xl sm:w-32" tabIndex={-1} aria-hidden>
        <ProgramCover url={item.coverImageUrl} title={item.title} />
      </Link>
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        <Link to={`/c/${item.coachSlug}/programs/${item.slug}`} className="line-clamp-2 font-semibold underline-offset-4 hover:underline">
          {item.title}
        </Link>
        {owned && <p className="text-sm text-muted-foreground">You already own this program; it is not charged.</p>}
        {blocked && <p className="text-sm text-destructive">This coach cannot take payments right now. Remove it to check out the rest.</p>}
        <div className="mt-auto flex items-center justify-between gap-3 pt-2">
          <span className={owned ? 'text-muted-foreground line-through' : 'font-heading text-lg font-extrabold tabular-nums'}>
            {formatMoney(item.priceAmount, item.currency)}
          </span>
          <Button variant="ghost" size="sm" onClick={onRemove} aria-label={`Remove ${item.title}`}>
            <Trash2 aria-hidden /> Remove
          </Button>
        </div>
      </div>
    </li>
  )
}

function Cancelled() {
  return (
    <Alert>
      <ShoppingBag aria-hidden />
      <AlertDescription>Checkout cancelled. You have not been charged, and your cart is still here.</AlertDescription>
    </Alert>
  )
}

// Why a checkout was refused and which programs it names (ProblemDetails extensions "reason" and
// "programIds").
function refusal(error: unknown): { reason?: string; programIds: string[] } {
  if (typeof error !== 'object' || error === null) return { programIds: [] }
  const reason = 'reason' in error && typeof error.reason === 'string' ? error.reason : undefined
  const ids = 'programIds' in error && Array.isArray(error.programIds) ? error.programIds.filter((id): id is string => typeof id === 'string') : []
  return { reason, programIds: ids }
}

