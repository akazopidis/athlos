import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Mail, Receipt } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { Empty, Loading, Section } from '@/components/page'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { useApp } from '@/lib/AppContext'
import { formatMoney } from '@/lib/money'
import { problemTitle } from '@/lib/problem'
import { contactLink, useCoaches } from '@/lib/useCoaches'
import { useTitle } from '@/lib/useTitle'
import { useMyOrders } from './useMyOrders.ts'

// The customer's orders across all coaches (design §8, "/me/orders"). A list rather than a table, so it
// reads the same on a phone. Each order links to its coach, and to their contact address when they set
// one. Within the refund window the customer asks for a refund here (design §2.2): the coach refunds it,
// or Athlos does after three working days.
export function MyOrders() {
  const { orders } = useMyOrders()
  const coaches = useCoaches(orders.data?.items.map((o) => o.coachSlug) ?? [])
  // When the page opened: the refund window is checked against it (the server checks again).
  const [now] = useState(() => Date.now())
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
                    {o.discountCode && ` · code ${o.discountCode} saved ${formatMoney(o.discountAmount, o.currency)}`}
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
                {o.checkoutId && (o.status === 'Paid' || o.status === 'Refunded') && (
                  <Link to={`/me/orders/${o.checkoutId}/receipt`} className="text-sm underline-offset-4 hover:underline" aria-label={`Receipt for ${o.programTitle}`}>
                    Receipt
                  </Link>
                )}
                {o.status === 'Paid' && <RefundRequest order={o} now={now} />}
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

type Order = { id: string; programTitle: string; refundableUntil?: string | null; refundRequestedAt?: string | null }

// "Request a refund" (design §2.2): while the refund window is open, with an optional note to the coach.
// Once asked, the order says so; the coach refunds it within three working days, or Athlos does.
function RefundRequest({ order: o, now }: { order: Order; now: number }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const [note, setNote] = useState('')
  const request = useMutation({
    mutationFn: async () => {
      const { error, response } = await api.POST('/api/v1/me/orders/{orderId}/refund-request', {
        params: { path: { orderId: o.id } },
        body: { note: note.trim() || null },
      })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not send the request.')
    },
    onSuccess: () => {
      setOpen(false)
      return queryClient.invalidateQueries({ queryKey: ['my-orders'] })
    },
  })

  if (o.refundRequestedAt) {
    return (
      <p className="basis-full text-sm text-muted-foreground">
        Refund requested on {new Date(o.refundRequestedAt).toLocaleDateString()}. The coach refunds it within three working days, or Athlos does.
      </p>
    )
  }
  if (!o.refundableUntil || new Date(o.refundableUntil).getTime() < now) return null
  if (!open) {
    return (
      <Button size="sm" variant="ghost" onClick={() => setOpen(true)} aria-label={`Request a refund for ${o.programTitle}`}>
        Request a refund
      </Button>
    )
  }
  return (
    <form
      className="basis-full space-y-2"
      onSubmit={(e) => {
        e.preventDefault()
        request.mutate()
      }}
    >
      <Textarea
        aria-label="Note to the coach (optional)"
        placeholder="Why? (optional, the coach sees it)"
        maxLength={500}
        rows={2}
        value={note}
        onChange={(e) => setNote(e.target.value)}
      />
      {request.error && (
        <Alert variant="destructive">
          <AlertDescription>{request.error.message}</AlertDescription>
        </Alert>
      )}
      <div className="flex gap-2">
        <Button type="submit" size="sm" disabled={request.isPending}>
          {request.isPending ? 'Sending…' : 'Send refund request'}
        </Button>
        <Button type="button" size="sm" variant="ghost" disabled={request.isPending} onClick={() => setOpen(false)}>
          Cancel
        </Button>
      </div>
      <p className="text-xs text-muted-foreground">You get the full amount back on your card, and your access to the program ends.</p>
    </form>
  )
}
