import { Tag } from 'lucide-react'
import type { components } from '@/api/catalog'
import { formatMoney } from '@/lib/money'
import { cn } from '@/lib/utils'

type Offer = components['schemas']['DiscountOffer']

// The code a coach shows on a program (design §2.4). Only an advert: the customer enters the code in
// the cart, and checkout applies it. The price with it is rounded like checkout (the discount down).
export function DiscountNote({ offer, price, currency, className }: { offer: Offer; price?: number; currency?: string; className?: string }) {
  const percent = Number(offer.percentOff)
  const until = offer.expiresAt ? new Date(offer.expiresAt).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) : null

  return (
    <p className={cn('flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-sm', className)}>
      <Tag className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
      <span>
        <span className="font-semibold">{percent}% off</span> <span className="text-muted-foreground">with code</span>{' '}
        <span className="rounded bg-volt px-1.5 font-mono text-xs font-bold tracking-wide text-volt-foreground">{offer.code}</span>
        {price !== undefined && currency && <span className="text-muted-foreground">: {formatMoney(price - Math.floor((price * percent) / 100), currency)}</span>}
        {until && <span className="text-muted-foreground"> · until {until}</span>}
      </span>
    </p>
  )
}
