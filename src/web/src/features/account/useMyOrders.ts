import { useQuery } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useSearchParams } from 'react-router'
import { useApp } from '@/lib/AppContext'
import { useCart } from '@/lib/cart'

// The customer's orders, and the ones of the checkout Stripe just returned them from
// (?checkout=success&id=<checkout id>; checkouts from before the cart used &order=<order id>).
// Orders turn Paid only when Stripe's webhook arrives, usually a moment later, so the list is
// re-read until none of them is Pending. Shared by "My orders" and "My programs".
export function useMyOrders() {
  const { api } = useApp()
  const cart = useCart()
  const [search] = useSearchParams()
  const success = search.get('checkout') === 'success'
  const checkoutId = success ? search.get('id') : null
  const orderId = success ? search.get('order') : null

  const orders = useQuery({
    queryKey: ['my-orders'],
    queryFn: async () => (await api.GET('/api/v1/me/orders')).data ?? null,
    refetchInterval: (query) => (fromCheckout(query.state.data?.items ?? [], checkoutId, orderId).some((o) => o.status === 'Pending') ? 2000 : false),
  })

  const returned = fromCheckout(orders.data?.items ?? [], checkoutId, orderId)

  // What was bought leaves the cart (programs added since, in another tab, stay).
  const bought = returned.map((o) => o.programId).join(',')
  const { remove } = cart
  useEffect(() => {
    if (bought) remove(...bought.split(','))
  }, [bought, remove])

  return { orders, returned }
}

type Order = { id: string; programId: string; checkoutId?: string | null; status: string }

function fromCheckout<T extends Order>(orders: T[], checkoutId: string | null, orderId: string | null): T[] {
  if (checkoutId) return orders.filter((o) => o.checkoutId === checkoutId)
  if (orderId) return orders.filter((o) => o.id === orderId)
  return []
}
