import { Mail, Receipt } from 'lucide-react'
import { Link } from 'react-router'
import { Empty, Loading, Section } from '@/components/page'
import { Badge } from '@/components/ui/badge'
import { formatMoney } from '@/lib/money'
import { contactLink, useCoaches } from '@/lib/useCoaches'
import { useTitle } from '@/lib/useTitle'
import { useMyOrders } from './useMyOrders.ts'

// The customer's orders across all coaches (design §8, "/me/orders"). A list rather than a table, so it
// reads the same on a phone. Each order links to its coach, and to their contact address when they set
// one (refunds are the coach's to give, within 14 days).
export function MyOrders() {
  const { orders } = useMyOrders()
  const coaches = useCoaches(orders.data?.items.map((o) => o.coachSlug) ?? [])
  useTitle('Orders')

  return (
    <Section title="Orders">
      {orders.isPending ? (
        <Loading />
      ) : orders.data?.items.length ? (
        <ul className="divide-y rounded-2xl border bg-card">
          {orders.data.items.map((o) => {
            const coach = coaches.get(o.coachSlug)
            const contact = contactLink(coach, `About my order: ${o.programTitle}`)
            return (
              <li key={o.id} className="flex flex-wrap items-center gap-x-4 gap-y-2 p-4">
                <div className="min-w-0 flex-1 basis-48">
                  <p className="truncate font-medium">{o.programTitle}</p>
                  <p className="text-sm text-muted-foreground">
                    <Link to={`/c/${o.coachSlug}`} className="underline-offset-4 hover:underline">
                      {coach?.displayName ?? o.coachSlug}
                    </Link>{' '}
                    · {new Date(o.paidAt ?? o.createdAt).toLocaleDateString()}
                    {contact && (
                      <>
                        {' · '}
                        <a href={contact} className="inline-flex items-center gap-1 underline-offset-4 hover:text-foreground hover:underline">
                          <Mail className="size-3.5" aria-hidden /> Contact coach
                        </a>
                      </>
                    )}
                  </p>
                </div>
                {/* An unpaid checkout stays Pending until Stripe expires it (24 hours), then disappears. */}
                <Badge variant={o.status === 'Paid' ? 'secondary' : 'outline'}>{o.status === 'Pending' ? 'Awaiting payment' : o.status}</Badge>
                <span className="w-20 text-right font-semibold tabular-nums">{formatMoney(o.amount, o.currency)}</span>
              </li>
            )
          })}
        </ul>
      ) : (
        <Empty icon={<Receipt />}>No orders yet.</Empty>
      )}
    </Section>
  )
}
