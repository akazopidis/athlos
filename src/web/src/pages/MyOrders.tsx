import { useQuery } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router'
import { useApp } from '../AppContext.tsx'
import { formatMoney } from '../money.ts'

// The customer's orders across all coaches (design §8, "/me").
// Stripe returns here with ?checkout=success&order=<id>. The order turns Paid only when Stripe's
// webhook arrives, usually a moment later, so the list is re-read until that order is no longer Pending.
export function MyOrders() {
  const { api } = useApp()
  const [search] = useSearchParams()
  const returnedOrderId = search.get('checkout') === 'success' ? search.get('order') : null

  const orders = useQuery({
    queryKey: ['my-orders'],
    queryFn: async () => (await api.GET('/api/v1/me/orders')).data ?? null,
    refetchInterval: (query) =>
      returnedOrderId && query.state.data?.items.find((o) => o.id === returnedOrderId)?.status === 'Pending' ? 2000 : false,
  })

  const returned = orders.data?.items.find((o) => o.id === returnedOrderId)

  return (
    <section>
      <h1>My orders</h1>
      {returned?.status === 'Pending' && <p role="status">Payment received. Confirming your order…</p>}
      {returned?.status === 'Paid' && <p role="status">Thank you! Your purchase of {returned.programTitle} is confirmed.</p>}
      {orders.isPending ? (
        <p>Loading…</p>
      ) : orders.data?.items.length ? (
        <table>
          <thead>
            <tr>
              <th>Program</th>
              <th>Price</th>
              <th>Status</th>
              <th>Date</th>
            </tr>
          </thead>
          <tbody>
            {orders.data.items.map((o) => (
              <tr key={o.id}>
                <td>
                  {o.programTitle} <Link to={`/c/${o.coachSlug}`}>({o.coachSlug})</Link>
                </td>
                <td>{formatMoney(o.amount, o.currency)}</td>
                <td>{o.status}</td>
                <td>{new Date(o.paidAt ?? o.createdAt).toLocaleDateString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <p>
          No orders yet. <Link to="/">Browse programs</Link>
        </p>
      )}
    </section>
  )
}
