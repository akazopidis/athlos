import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { CartProvider } from '@/lib/CartProvider'
import { signedInAs } from '@/test-auth'
import { MyPrograms } from './MyPrograms.tsx'

afterEach(() => vi.unstubAllGlobals())

const mine = (programId: string, title: string, extra: object = {}) => ({
  programId,
  programTitle: title,
  coachSlug: 'anna',
  enrolledAt: '2026-10-01T00:00:00Z',
  completedWorkouts: 0,
  reviewed: false,
  ...extra,
})

// "My programs" (design §8) against fake enrollment and ordering APIs; `url` is where the page opens,
// for the return from Stripe.
function renderMyPrograms(programs: object[], orders: object[] = [], url = '/me') {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname
      if (path === '/api/v1/me/programs') return Response.json({ items: programs, page: 1, pageSize: 20, totalCount: programs.length })
      if (path === '/api/v1/me/orders') return Response.json({ items: orders, page: 1, pageSize: 20, totalCount: orders.length })
      return Response.json({ items: [] })
    }),
  )
  const auth = signedInAs('customer')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <CartProvider>
          <MemoryRouter initialEntries={[url]}>
            <MyPrograms />
          </MemoryRouter>
        </CartProvider>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
}

test('each program shows its progress, and the ones not reviewed yet ask for a rating', async () => {
  renderMyPrograms([mine('p-1', 'Strong start', { completedWorkouts: 3, reviewed: true }), mine('p-2', 'Run club')])

  const started = (await screen.findByText('Strong start')).closest('li') as HTMLElement
  const fresh = screen.getByText('Run club').closest('li') as HTMLElement
  expect(within(started).getByText('3 workouts done')).toBeInTheDocument()
  expect(within(started).queryByText('Rate this program')).toBeNull()
  expect(within(fresh).getByText('Not started yet')).toBeInTheDocument()
  expect(within(fresh).getByRole('link', { name: /Rate this program/ })).toHaveAttribute('href', '/me/programs/p-2#review')
})

// Back from Stripe: a paid order whose access enrollment-api has not created yet is named while it is set up.
test('a program just paid for is announced until its access exists', async () => {
  renderMyPrograms(
    [mine('p-1', 'Strong start')],
    [{ id: 'o-2', checkoutId: 'c-1', programId: 'p-2', programTitle: 'Run club', status: 'Paid', priceAmount: 2900, currency: 'EUR' }],
    '/me?checkout=success&id=c-1',
  )

  expect(await screen.findByText('Setting up your access to Run club…')).toBeInTheDocument()
})

test('without programs the page points to the shop', async () => {
  renderMyPrograms([])

  expect(await screen.findByText('No programs yet')).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Browse programs' })).toHaveAttribute('href', '/')
})
