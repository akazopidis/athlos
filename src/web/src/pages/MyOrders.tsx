import { Link } from 'react-router'
import { formatMoney } from '../money.ts'
import { useMyOrders } from './useMyOrders.ts'

// The customer's orders across all coaches (design §8, "/me").
export function MyOrders() {
  const { orders, returned } = useMyOrders()

  return (
    <section>
      <h2>My orders</h2>
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
