import { Check, ShoppingBag } from 'lucide-react'
import { Link } from 'react-router'
import type { components } from '@/api/catalog'
import { ProgramCover } from '@/components/program-cover'
import { Button } from '@/components/ui/button'
import { MaxCartItems, useCart } from '@/lib/cart'
import { categoryLabel } from '@/lib/categories'
import { readableOn } from '@/lib/color'
import type { Coach } from '@/lib/useCoaches'
import { formatMoney } from '@/lib/money'

type Program = components['schemas']['SearchProgramsItem']

// A program as a shop product: cover, category and length, the coach, the price, and a quick
// "Add to cart".
// mine: a program of a workspace the visitor coaches in; it is theirs in the studio, not for sale to them.
export function ProductCard({ program: p, coach, owned, mine = false }: { program: Program; coach?: Coach; owned: boolean; mine?: boolean }) {
  const weeks = Number(p.durationWeeks)
  const href = `/c/${p.coachSlug}/programs/${p.slug}`
  const coachName = coach?.displayName ?? p.coachSlug

  return (
    <article className="group relative flex h-full flex-col overflow-hidden rounded-2xl border bg-card transition-shadow hover:shadow-lg">
      <Link to={href} className="block outline-none focus-visible:ring-3 focus-visible:ring-ring/50" tabIndex={-1} aria-hidden>
        <div className="relative overflow-hidden bg-muted">
          <ProgramCover url={p.coverImageUrl} title={p.title} category={p.category} className="transition-transform duration-500 ease-out group-hover:scale-[1.04]" />
          <div className="absolute top-3 left-3 flex gap-1.5">
            <span className="rounded-full bg-background/90 px-2.5 py-1 text-xs font-semibold shadow-sm backdrop-blur">{categoryLabel(p.category)}</span>
            <span className="rounded-full bg-background/90 px-2.5 py-1 text-xs font-semibold shadow-sm backdrop-blur">
              {weeks} {weeks === 1 ? 'week' : 'weeks'}
            </span>
          </div>
          {owned && (
            <span className="absolute top-3 right-3 flex items-center gap-1 rounded-full bg-volt px-2.5 py-1 text-xs font-bold text-volt-foreground shadow-sm">
              <Check className="size-3.5" aria-hidden /> Owned
            </span>
          )}
        </div>
      </Link>
      <div className="flex flex-1 flex-col gap-3 p-4">
        <div className="flex items-center gap-2 text-sm text-muted-foreground">
          <CoachMark coach={coach} name={coachName} />
          <Link to={`/c/${p.coachSlug}`} className="truncate hover:text-foreground hover:underline">
            {coachName}
          </Link>
        </div>
        <h3 className="line-clamp-2 text-base leading-snug font-bold">
          <Link to={href} className="outline-none after:absolute after:inset-0 after:content-[''] focus-visible:underline">
            {p.title}
          </Link>
        </h3>
        <div className="mt-auto flex items-center justify-between gap-3">
          <span className="font-heading text-xl font-black tabular-nums">{formatMoney(p.priceAmount, p.currency)}</span>
          {/* Above the card's stretched link, so it gets its own clicks. */}
          <div className="relative z-10">{mine ? <StudioLink slug={p.coachSlug} /> : owned ? <OwnedLink programId={p.id} /> : <AddToCart program={p} />}</div>
        </div>
      </div>
    </article>
  )
}

function StudioLink({ slug }: { slug: string }) {
  return (
    <Button asChild size="sm" variant="outline">
      <Link to={`/studio/${slug}`}>Your program</Link>
    </Button>
  )
}

// The customer's own copy of the program, with their progress.
function OwnedLink({ programId }: { programId: string }) {
  return (
    <Button asChild size="sm" variant="outline">
      <Link to={`/me/programs/${programId}`}>Open</Link>
    </Button>
  )
}

function AddToCart({ program: p }: { program: Program }) {
  const cart = useCart()
  const inCart = cart.has(p.id)
  const full = !inCart && cart.items.length >= MaxCartItems

  return (
    <Button
      size="sm"
      variant={inCart ? 'secondary' : 'default'}
      disabled={full}
      title={full ? `A cart holds up to ${MaxCartItems} programs` : undefined}
      aria-label={inCart ? `${p.title} is in your cart` : `Add ${p.title} to cart`}
      onClick={() =>
        inCart
          ? cart.remove(p.id)
          : cart.add({
              programId: p.id,
              coachSlug: p.coachSlug,
              slug: p.slug,
              title: p.title,
              priceAmount: Number(p.priceAmount),
              currency: p.currency,
              coverImageUrl: p.coverImageUrl,
            })
      }
    >
      {inCart ? <Check aria-hidden /> : <ShoppingBag aria-hidden />}
      {inCart ? 'In cart' : 'Add'}
    </Button>
  )
}

// The coach's logo, or their initial on their brand color.
export function CoachMark({ coach, name, className = 'size-6' }: { coach?: Coach; name: string; className?: string }) {
  if (coach?.branding.logoUrl) return <img src={coach.branding.logoUrl} alt="" className={`${className} shrink-0 rounded-full object-cover`} />
  return (
    <span
      aria-hidden
      className={`${className} flex shrink-0 items-center justify-center rounded-full bg-ink text-[0.65rem] font-bold text-ink-foreground`}
      style={coach?.branding.primaryColor ? { background: coach.branding.primaryColor, color: readableOn(coach.branding.primaryColor) } : undefined}
    >
      {name.slice(0, 1).toUpperCase()}
    </span>
  )
}
