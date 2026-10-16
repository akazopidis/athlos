import type { PublicClientApplication } from '@azure/msal-browser'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { CartProvider } from '@/lib/CartProvider'
import { leaveTo } from '@/lib/navigation'
import { ProgramPage } from './ProgramPage.tsx'

vi.mock('@/lib/navigation', () => ({ leaveTo: vi.fn() }))

afterEach(() => {
  vi.unstubAllGlobals()
  vi.mocked(leaveTo).mockReset()
  localStorage.clear()
})

const program = {
  id: '0199a000-0000-7000-8000-000000000001',
  coachSlug: 'anna',
  slug: 'strong-start',
  title: 'Strong start',
  description: 'Eight weeks of basics.',
  category: 'Powerlifting',
  durationWeeks: 8,
  priceAmount: 4900,
  currency: 'EUR',
  coverImageUrl: null,
}

// A signed-in customer: enough of MSAL for getAccessToken.
const signedIn = {
  getActiveAccount: () => ({ username: 'customer' }),
  acquireTokenSilent: async () => ({ accessToken: 'token' }),
} as unknown as PublicClientApplication

function renderPage(checkout: (request: Request) => Response, owned: string[] = [], workspaces: string[] = []) {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      if (request.method === 'POST') return checkout(request)
      if (new URL(request.url).pathname === '/api/v1/marketplace/coaches/anna') {
        return Response.json({ slug: 'anna', displayName: 'Anna Coaching', branding: { logoUrl: null, primaryColor: '#1d4ed8' } })
      }
      if (new URL(request.url).pathname === '/api/v1/me/programs') {
        return Response.json({ items: owned.map((programId) => ({ programId })), page: 1, pageSize: 20, totalCount: owned.length })
      }
      if (new URL(request.url).pathname === '/api/v1/marketplace/programs') {
        return Response.json({ items: [], page: 1, pageSize: 5, totalCount: 0, coaches: [] })
      }
      if (new URL(request.url).pathname === '/api/v1/marketplace/coaches') return Response.json([])
      if (new URL(request.url).pathname === '/api/v1/me/memberships') {
        return Response.json(workspaces.map((slug) => ({ tenantId: 't', slug, displayName: slug, role: 'Owner', status: 'Active' })))
      }
      return Response.json(program)
    }),
  )

  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, msal: signedIn, api: createApi(signedIn, config.apiScope) }}>
      <QueryClientProvider client={new QueryClient()}>
        <CartProvider>
          <MemoryRouter initialEntries={['/c/anna/programs/strong-start']}>
            <Routes>
              <Route path="/c/:slug/programs/:programSlug" element={<ProgramPage />} />
            </Routes>
          </MemoryRouter>
        </CartProvider>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('Buy now starts a checkout of this program alone and leaves for Stripe', async () => {
  const requests = renderPage(() => Response.json({ checkoutId: 'checkout', checkoutUrl: 'https://checkout.stripe.test/s' }))

  expect(await screen.findByText('€49.00')).toBeInTheDocument()
  // The coach's name, from the storefront, links back to it (the breadcrumb and the byline).
  for (const link of await screen.findAllByRole('link', { name: 'Anna Coaching' })) expect(link).toHaveAttribute('href', '/c/anna')
  fireEvent.click(screen.getByRole('button', { name: 'Buy now' }))

  await waitFor(() => expect(leaveTo).toHaveBeenCalledWith('https://checkout.stripe.test/s'))
  const checkout = requests.find((r) => r.method === 'POST')!
  expect(new URL(checkout.url).pathname).toBe('/api/v1/orders/checkout')
  expect(await checkout.json()).toEqual({ programIds: [program.id] })
  expect(checkout.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/)
  expect(checkout.headers.get('Authorization')).toBe('Bearer token')
})

test('shows why the server refused the checkout', async () => {
  renderPage(() =>
    Response.json({ title: 'You already own this program.', status: 409 }, { status: 409, headers: { 'Content-Type': 'application/problem+json' } }),
  )

  fireEvent.click(await screen.findByRole('button', { name: 'Buy now' }))

  expect(await screen.findByRole('alert')).toHaveTextContent('You already own this program.')
  expect(leaveTo).not.toHaveBeenCalled()
})

test('a customer who already owns the program gets Open program instead of Buy', async () => {
  renderPage(() => Response.json({}), [program.id])

  expect(await screen.findByRole('link', { name: 'Open program' })).toHaveAttribute('href', `/me/programs/${program.id}`)
  expect(screen.queryByRole('button', { name: 'Buy now' })).toBeNull()
  expect(screen.queryByRole('button', { name: /Add to cart/ })).toBeNull()
})

test("a coach sees their own workspace's program as theirs, not for sale", async () => {
  renderPage(() => Response.json({}), [], ['anna'])

  expect(await screen.findByRole('link', { name: 'Open in studio' })).toHaveAttribute('href', '/studio/anna')
  expect(screen.queryByRole('button', { name: /Add to cart/ })).toBeNull()
  expect(screen.queryByRole('button', { name: 'Buy now' })).toBeNull()
})

test('Add to cart keeps the program in the cart, in this browser', async () => {
  renderPage(() => Response.json({}))

  fireEvent.click(await screen.findByRole('button', { name: /Add to cart/ }))

  expect(await screen.findByRole('link', { name: /In cart/ })).toHaveAttribute('href', '/cart')
  expect(JSON.parse(localStorage.getItem('athlos.cart')!)).toEqual([expect.objectContaining({ programId: program.id, title: 'Strong start', priceAmount: 4900 })])
})
