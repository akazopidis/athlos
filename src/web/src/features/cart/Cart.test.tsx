import type { PublicClientApplication } from '@azure/msal-browser'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { CartProvider } from '@/lib/CartProvider'
import { leaveTo } from '@/lib/navigation'
import { Cart } from './Cart.tsx'

vi.mock('@/lib/navigation', () => ({ leaveTo: vi.fn() }))

const item = (id: string, coachSlug: string, title: string, priceAmount: number) => ({
  programId: id,
  coachSlug,
  slug: title.toLowerCase().replace(/ /g, '-'),
  title,
  priceAmount,
  currency: 'EUR',
})

const annaProgram = item('0199a000-0000-7000-8000-00000000000a', 'anna', 'Strong start', 4900)
const benProgram = item('0199a000-0000-7000-8000-00000000000b', 'ben', 'Run ten k', 2000)

beforeEach(() => localStorage.setItem('athlos.cart', JSON.stringify([annaProgram, benProgram])))

afterEach(() => {
  vi.unstubAllGlobals()
  vi.mocked(leaveTo).mockReset()
  localStorage.clear()
})

const signedIn = {
  getActiveAccount: () => ({ username: 'customer', localAccountId: 'customer' }),
  acquireTokenSilent: async () => ({ accessToken: 'token' }),
} as unknown as PublicClientApplication

function renderCart(checkout: () => Response) {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      const path = new URL(request.url).pathname
      if (request.method === 'POST') return checkout()
      if (path === '/api/v1/me/programs') return Response.json({ items: [], page: 1, pageSize: 20, totalCount: 0 })
      return Response.json([
        { slug: 'anna', displayName: 'Anna Coaching', branding: {} },
        { slug: 'ben', displayName: 'Ben Runs', branding: {} },
      ])
    }),
  )
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, msal: signedIn, api: createApi(signedIn, config.apiScope) }}>
      <QueryClientProvider client={new QueryClient()}>
        <CartProvider>
          <MemoryRouter initialEntries={['/cart']}>
            <Cart />
          </MemoryRouter>
        </CartProvider>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('programs from two coaches are checked out together, in one payment', async () => {
  const requests = renderCart(() => Response.json({ checkoutId: 'checkout', checkoutUrl: 'https://checkout.stripe.test/cart' }))

  expect(await screen.findByRole('region', { name: 'From Anna Coaching' })).toBeInTheDocument()
  expect(screen.getByRole('region', { name: 'From Ben Runs' })).toBeInTheDocument()
  expect(screen.getByText('€69.00')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Check out' }))

  await waitFor(() => expect(leaveTo).toHaveBeenCalledWith('https://checkout.stripe.test/cart'))
  const post = requests.find((r) => r.method === 'POST')!
  expect(new URL(post.url).pathname).toBe('/api/v1/orders/checkout')
  expect(await post.json()).toEqual({ programIds: [annaProgram.programId, benProgram.programId] })
  expect(post.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/)
})

test('a program no longer for sale is taken out of the cart, and the customer is told', async () => {
  renderCart(() =>
    Response.json(
      { title: 'Some programs in your cart are no longer for sale.', status: 404, reason: 'notForSale', programIds: [benProgram.programId] },
      { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  )

  fireEvent.click(await screen.findByRole('button', { name: 'Check out' }))

  expect(await screen.findByRole('status')).toHaveTextContent('Removed from your cart: Run ten k.')
  expect(screen.queryByText('Run ten k')).toBeNull()
  expect(JSON.parse(localStorage.getItem('athlos.cart')!)).toEqual([annaProgram])
  expect(leaveTo).not.toHaveBeenCalled()
})

test('a coach who cannot take payments is marked, and the cart stays', async () => {
  renderCart(() =>
    Response.json(
      { title: 'ben cannot take payments right now.', status: 409, reason: 'cannotSell', programIds: [benProgram.programId] },
      { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  )

  fireEvent.click(await screen.findByRole('button', { name: 'Check out' }))

  expect(await screen.findByText(/This coach cannot take payments right now/)).toBeInTheDocument()
  expect(screen.getByRole('alert')).toHaveTextContent('ben cannot take payments right now.')
  expect(JSON.parse(localStorage.getItem('athlos.cart')!)).toHaveLength(2)
})
