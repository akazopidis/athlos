import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { useApp } from '@/lib/AppContext'

// The customer's orders, and the one Stripe just returned them from (?checkout=success&order=<id>).
// The order turns Paid only when Stripe's webhook arrives, usually a moment later, so the list is
// re-read until that order is no longer Pending. Shared by "My orders" and "My programs".
export function useMyOrders() {
  const { api } = useApp()
  const [search] = useSearchParams()
  const returnedOrderId = search.get('checkout') === 'success' ? search.get('order') : null

  const orders = useQuery({
    queryKey: ['my-orders'],
    queryFn: async () => (await api.GET('/api/v1/me/orders')).data ?? null,
    refetchInterval: (query) =>
      returnedOrderId && query.state.data?.items.find((o) => o.id === returnedOrderId)?.status === 'Pending' ? 2000 : false,
  })

  return { orders, returned: orders.data?.items.find((o) => o.id === returnedOrderId) }
}
