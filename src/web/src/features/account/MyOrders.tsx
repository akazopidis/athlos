import { CircleCheck, Loader2, Receipt } from 'lucide-react'
import { Link } from 'react-router'
import { Empty, Loading, Section } from '@/components/page'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { formatMoney } from '@/lib/money'
import { useMyOrders } from './useMyOrders.ts'

// The customer's orders across all coaches (design §8, "/me"). A list rather than a table, so it
// reads the same on a phone.
export function MyOrders() {
  const { orders, returned } = useMyOrders()

  return (
    <Section title="My orders">
      {returned?.status === 'Pending' && (
        <Alert>
          <Loader2 className="animate-spin" aria-hidden />
          <AlertDescription>Payment received. Confirming your order…</AlertDescription>
        </Alert>
      )}
      {returned?.status === 'Paid' && (
        <Alert>
          <CircleCheck aria-hidden />
          <AlertDescription>Thank you! Your purchase of {returned.programTitle} is confirmed.</AlertDescription>
        </Alert>
      )}
      {orders.isPending ? (
        <Loading />
      ) : orders.data?.items.length ? (
        <ul className="divide-y rounded-2xl border bg-card">
          {orders.data.items.map((o) => (
            <li key={o.id} className="flex flex-wrap items-center gap-x-4 gap-y-2 p-4">
              <div className="min-w-0 flex-1 basis-48">
                <p className="truncate font-medium">{o.programTitle}</p>
                <p className="text-sm text-muted-foreground">
                  <Link to={`/c/${o.coachSlug}`} className="underline-offset-4 hover:underline">
                    {o.coachSlug}
                  </Link>{' '}
                  · {new Date(o.paidAt ?? o.createdAt).toLocaleDateString()}
                </p>
              </div>
              {/* An unpaid checkout stays Pending until Stripe expires it (24 hours), then disappears. */}
              <Badge variant={o.status === 'Paid' ? 'secondary' : 'outline'}>{o.status === 'Pending' ? 'Awaiting payment' : o.status}</Badge>
              <span className="w-20 text-right font-semibold tabular-nums">{formatMoney(o.amount, o.currency)}</span>
            </li>
          ))}
        </ul>
      ) : (
        <Empty icon={<Receipt />}>No orders yet.</Empty>
      )}
    </Section>
  )
}
