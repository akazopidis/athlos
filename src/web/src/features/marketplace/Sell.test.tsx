import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { Sell } from './Sell.tsx'

afterEach(() => vi.unstubAllGlobals())

test('the sell page shows the fee, the refund window and the plans from the API', async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Response.json([
        { name: 'Free', requestsPerSecond: 20, maxPrograms: 5, maxStaff: 1, maxMediaGigabytes: 1 },
        { name: 'Pro', requestsPerSecond: 100, maxPrograms: 100, maxStaff: 10, maxMediaGigabytes: 20 },
      ]),
    ),
  )
  const config = { entraClientId: null, entraAuthority: null, apiScope: null, version: 'test', refundWindowDays: 14, platformFeePercent: 10 }
  render(
    <AppContext.Provider value={{ config, auth: null, api: createApi(null) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter>
          <Sell />
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )

  expect(await screen.findByText('Up to 100 programs')).toBeInTheDocument()
  expect(screen.getByText('Up to 5 programs')).toBeInTheDocument()
  expect(screen.getByText('You keep 90% of every sale')).toBeInTheDocument()
  expect(screen.getByRole('heading', { name: 'No monthly fee. 10% per sale.' })).toBeInTheDocument()
  expect(screen.getByText(/refund any sale within 14 days/)).toBeInTheDocument()
})
