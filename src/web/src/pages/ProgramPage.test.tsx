import type { PublicClientApplication } from '@azure/msal-browser'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '../api/client.ts'
import { AppContext } from '../AppContext.tsx'
import { leaveTo } from '../navigation.ts'
import { ProgramPage } from './ProgramPage.tsx'

vi.mock('../navigation.ts', () => ({ leaveTo: vi.fn() }))

afterEach(() => {
  vi.unstubAllGlobals()
  vi.mocked(leaveTo).mockReset()
})

const program = {
  id: '0199a000-0000-7000-8000-000000000001',
  coachSlug: 'anna',
  slug: 'strong-start',
  title: 'Strong start',
  description: 'Eight weeks of basics.',
  level: 'Beginner',
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

function renderPage(checkout: (request: Request) => Response) {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      return request.method === 'POST' ? checkout(request) : Response.json(program)
    }),
  )

  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, msal: signedIn, api: createApi(signedIn, config.apiScope) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={['/c/anna/programs/strong-start']}>
          <Routes>
            <Route path="/c/:slug/programs/:programSlug" element={<ProgramPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('Buy starts checkout for the program and leaves for Stripe', async () => {
  const requests = renderPage(() => Response.json({ orderId: 'order', checkoutUrl: 'https://checkout.stripe.test/s' }))

  expect(await screen.findByText('€49.00')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Buy' }))

  await waitFor(() => expect(leaveTo).toHaveBeenCalledWith('https://checkout.stripe.test/s'))
  const checkout = requests.find((r) => r.method === 'POST')!
  expect(new URL(checkout.url).pathname).toBe('/api/v1/orders/checkout')
  expect(await checkout.json()).toEqual({ programId: program.id })
  expect(checkout.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/)
  expect(checkout.headers.get('Authorization')).toBe('Bearer token')
})

test('shows why the server refused the checkout', async () => {
  renderPage(() =>
    Response.json({ title: 'You already own this program.', status: 409 }, { status: 409, headers: { 'Content-Type': 'application/problem+json' } }),
  )

  fireEvent.click(await screen.findByRole('button', { name: 'Buy' }))

  expect(await screen.findByRole('alert')).toHaveTextContent('You already own this program.')
  expect(leaveTo).not.toHaveBeenCalled()
})
