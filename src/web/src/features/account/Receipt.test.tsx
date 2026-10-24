import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { Receipt } from './Receipt.tsx'

afterEach(() => vi.unstubAllGlobals())

const signedIn = signedInAs('customer')

test('the receipt lists each program, the discount, the total and a refund', async () => {
  const requests: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(new URL(request.url).pathname)
      return Response.json({
        checkoutId: 'c1',
        paidAt: '2026-10-19T10:00:00Z',
        currency: 'EUR',
        lines: [
          { orderId: 'o1', programTitle: 'Strong start', coachSlug: 'anna', price: 2000, discountCode: 'HALF', discountAmount: 1000, amount: 1000, status: 'Paid', refundedAt: null },
          { orderId: 'o2', programTitle: 'Run ten k', coachSlug: 'ben', price: 3000, discountCode: null, discountAmount: 0, amount: 3000, status: 'Refunded', refundedAt: '2026-10-20T10:00:00Z' },
        ],
        subtotal: 5000,
        discount: 1000,
        total: 4000,
        refunded: 3000,
      })
    }),
  )

  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth: signedIn, api: createApi(signedIn) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={['/me/orders/c1/receipt']}>
          <Routes>
            <Route path="/me/orders/:checkoutId/receipt" element={<Receipt />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )

  expect(await screen.findByRole('heading', { name: 'Receipt' })).toBeInTheDocument()
  expect(requests).toContain('/api/v1/me/orders/receipts/c1')
  expect(screen.getByText('Code HALF: −€10.00')).toBeInTheDocument()
  expect(screen.getByText('€40.00')).toBeInTheDocument()
  expect(screen.getByText('−€30.00')).toBeInTheDocument()
  expect(screen.getByText(/^Refunded \d/)).toBeInTheDocument()
  expect(screen.getByRole('button', { name: /Print or save as PDF/ })).toBeInTheDocument()
})
