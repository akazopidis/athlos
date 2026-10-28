import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { StudioPayouts } from './StudioPayouts.tsx'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

const history = {
  available: [{ amount: 4410, currency: 'EUR' }],
  pending: [{ amount: 1764, currency: 'EUR' }],
  payouts: [
    { amount: 8820, currency: 'EUR', arrivalDate: '2026-10-27T00:00:00Z', status: 'paid' },
    { amount: 4410, currency: 'EUR', arrivalDate: '2026-10-29T00:00:00Z', status: 'in_transit' },
  ],
}

// Payouts in the studio (backlog item 42) against a fake tenancy-api: requests are recorded.
function renderPayouts(isOwner: boolean) {
  const requests: { method: string; path: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname
      requests.push({ method: request.method, path })
      if (path.endsWith('/payouts/history')) return Response.json(history)
      return Response.json({ url: 'https://connect.stripe.test/express/acct_1' })
    }),
  )
  const auth = signedInAs(isOwner ? 'owner' : 'staff')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient()}>
        <StudioPayouts slug="anna" isOwner={isOwner} />
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('the owner sees the balance and latest payouts, and opens the Stripe dashboard', async () => {
  const assign = vi.fn()
  vi.stubGlobal('location', { ...window.location, assign })
  const requests = renderPayouts(true)

  expect(await screen.findByText('Latest payouts to your bank')).toBeInTheDocument()
  expect(screen.getByText('Ready to pay out').nextSibling).toHaveTextContent('44.10')
  expect(screen.getByText('Still arriving').nextSibling).toHaveTextContent('17.64')
  expect(screen.getByText('Paid')).toBeInTheDocument()
  expect(screen.getByText('On its way')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: /Open Stripe dashboard/ }))

  await waitFor(() => expect(assign).toHaveBeenCalledWith('https://connect.stripe.test/express/acct_1'))
  expect(requests).toContainEqual({ method: 'POST', path: '/api/v1/studio/anna/payouts/dashboard' })
})

test('staff see that payouts work, but not the owner’s money', async () => {
  const requests = renderPayouts(false)

  expect(screen.getByText('Payouts are set up. You can publish programs.')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /Open Stripe dashboard/ })).toBeNull()
  await new Promise((resolve) => setTimeout(resolve, 50))
  expect(requests).toEqual([])
})
