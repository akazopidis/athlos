import type { PublicClientApplication } from '@azure/msal-browser'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { StudioSales } from './StudioSales.tsx'

afterEach(() => vi.unstubAllGlobals())

const sale = (id: string, status: 'Paid' | 'Refunded', refundableUntil = '2999-01-01T00:00:00Z') => ({
  id,
  programId: 'p',
  programTitle: `Program ${id}`,
  customerEmail: 'buyer@example.test',
  amount: 4900,
  platformFee: 490,
  currency: 'EUR',
  paidAt: '2026-10-11T10:00:00Z',
  status,
  refundedAt: status === 'Refunded' ? '2026-10-12T10:00:00Z' : null,
  refundableUntil,
})

const signedIn = {
  getActiveAccount: () => ({ username: 'coach' }),
  acquireTokenSilent: async () => ({ accessToken: 'token' }),
} as unknown as PublicClientApplication

function renderSales(role: 'Owner' | 'Staff') {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      const path = new URL(request.url).pathname
      if (request.method === 'POST') return new Response(null, { status: 204 })
      if (path === '/api/v1/me/memberships') return Response.json([{ tenantId: 't', slug: 'anna', displayName: 'Anna', role, status: 'Active' }])
      if (path.endsWith('/sales')) {
        return Response.json({ items: [sale('a', 'Paid'), sale('b', 'Refunded'), sale('c', 'Paid', '2026-01-01T00:00:00Z')], page: 1, pageSize: 20, totalCount: 3 })
      }
      return Response.json({ slug: 'anna', displayName: 'Anna' })
    }),
  )
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, msal: signedIn, api: createApi(signedIn, config.apiScope) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={['/studio/anna/sales']}>
          <Routes>
            <Route path="/studio/:slug/sales" element={<StudioSales />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('the owner refunds a sale after confirming, and refunded sales earn nothing', async () => {
  const requests = renderSales('Owner')

  expect(await screen.findByText('Refunded')).toBeInTheDocument()
  // Only the paid sales count: twice €49.00 minus the €4.90 fee.
  expect(screen.getByText('€88.20', { selector: 'div' })).toBeInTheDocument()
  // An old sale is past the refund window.
  expect(screen.queryByRole('button', { name: 'Refund Program c' })).toBeNull()
  expect(screen.getByText('Refund window closed')).toBeInTheDocument()
  fireEvent.click(await screen.findByRole('button', { name: 'Refund Program a' }))
  fireEvent.click(screen.getByRole('button', { name: 'Refund €49.00' }))

  await waitFor(() => expect(requests.some((r) => r.method === 'POST')).toBe(true))
  expect(new URL(requests.find((r) => r.method === 'POST')!.url).pathname).toBe('/api/v1/studio/anna/sales/a/refund')
})

test('staff see the sales but cannot refund', async () => {
  renderSales('Staff')

  expect(await screen.findByText('Program a')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /Refund/ })).toBeNull()
})
