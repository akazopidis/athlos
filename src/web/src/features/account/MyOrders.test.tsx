import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { CartProvider } from '@/lib/CartProvider'
import { signedInAs } from '@/test-auth'
import { MyOrders } from './MyOrders.tsx'

afterEach(() => vi.unstubAllGlobals())

const signedIn = signedInAs('customer')
const day = 24 * 60 * 60 * 1000
const order = (id: string, title: string, extra: object) => ({
  id,
  programId: `p-${id}`,
  programTitle: title,
  coachSlug: 'anna',
  amount: 2000,
  currency: 'EUR',
  status: 'Paid',
  createdAt: new Date(Date.now() - day).toISOString(),
  paidAt: new Date(Date.now() - day).toISOString(),
  checkoutId: null,
  discountCode: null,
  discountAmount: 0,
  refundableUntil: new Date(Date.now() + 13 * day).toISOString(),
  refundRequestedAt: null,
  ...extra,
})

function renderOrders(items: object[]) {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request)
      const path = new URL(request.url).pathname
      if (path === '/api/v1/me/orders') return Response.json({ items, page: 1, pageSize: 20, totalCount: items.length })
      if (path.endsWith('/refund-request')) return new Response(null, { status: 204 })
      if (path === '/api/v1/me/cart') return Response.json({ lines: [] })
      return Response.json([])
    }),
  )
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth: signedIn, api: createApi(signedIn) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter>
          <CartProvider>
            <MyOrders />
          </CartProvider>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('a paid order in the refund window can be refunded on request, with a note', async () => {
  const requests = renderOrders([order('o1', 'Strong start', {})])

  fireEvent.click(await screen.findByRole('button', { name: 'Request a refund for Strong start' }))
  fireEvent.change(screen.getByLabelText('Note to the coach (optional)'), { target: { value: '  Too hard for me  ' } })
  fireEvent.click(screen.getByRole('button', { name: 'Send refund request' }))

  await waitFor(() => expect(requests.some((r) => r.method === 'POST')).toBe(true))
  const post = requests.find((r) => r.method === 'POST')!
  expect(new URL(post.url).pathname).toBe('/api/v1/me/orders/o1/refund-request')
  expect(await post.json()).toEqual({ note: 'Too hard for me' })
})

test('a requested order says so, and an order past the window offers nothing', async () => {
  renderOrders([
    order('o1', 'Strong start', { refundRequestedAt: new Date().toISOString() }),
    order('o2', 'Run ten k', { refundableUntil: new Date(Date.now() - day).toISOString() }),
  ])

  expect(await screen.findByText(/Refund requested on/)).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /Request a refund/ })).not.toBeInTheDocument()
})
