import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { StudioOverview } from './StudioOverview.tsx'

afterEach(() => vi.unstubAllGlobals())

const program = {
  id: 'p-1',
  slug: 'strong-start',
  title: 'Strong start',
  category: 'Powerlifting',
  durationWeeks: 8,
  priceAmount: 4900,
  currency: 'EUR',
  isPublished: true,
  coverImageUrl: null,
}

interface Studio {
  payouts: object
  programs: object[]
  summary?: object
  reviews?: object
  role?: 'Owner' | 'Staff'
  onboarding?: Response
}

// The studio's first tab (design §8) against fake tenancy, catalog, ordering and enrollment APIs.
function renderOverview(studio: Studio) {
  const requests: { method: string; path: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname
      requests.push({ method: request.method, path })
      if (path === '/api/v1/studio/anna/workspace') return Response.json({ slug: 'anna', displayName: 'Anna', plan: 'Free', branding: {} })
      if (path === '/api/v1/me/memberships') return Response.json([{ slug: 'anna', role: studio.role ?? 'Owner' }])
      if (path === '/api/v1/studio/anna/payouts/onboarding') return studio.onboarding ?? Response.json({ url: 'https://connect.stripe.test/setup' })
      if (path === '/api/v1/studio/anna/payouts/history') return Response.json({ available: [], pending: [], payouts: [] })
      if (path === '/api/v1/studio/anna/payouts') return Response.json(studio.payouts)
      if (path === '/api/v1/studio/anna/programs') return Response.json(studio.programs)
      if (path === '/api/v1/studio/anna/sales/summary') return Response.json(studio.summary ?? { sales: 0 })
      if (path === '/api/v1/studio/anna/reviews') return Response.json(studio.reviews ?? { items: [], page: 1, pageSize: 2, totalCount: 0, average: 0, reviewCount: 0 })
      if (path === '/api/v1/plans') return Response.json([{ name: 'Free', maxPrograms: 3 }])
      return new Response(null, { status: 404 })
    }),
  )
  const auth = signedInAs('owner')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/studio/anna']}>
          <Routes>
            <Route path="/studio/:slug" element={<StudioOverview />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('a new workspace gets the setup checklist and the call to set up payouts', async () => {
  renderOverview({ payouts: { connected: false, onboardingComplete: false }, programs: [] })

  const checklist = (await screen.findByRole('heading', { name: 'Get started' })).closest('section') as HTMLElement
  expect(within(checklist).getAllByText('(to do)')).toHaveLength(3)
  expect(screen.getByRole('button', { name: 'Set up payouts' })).toBeInTheDocument()
  expect(screen.queryByRole('heading', { name: 'Your numbers' })).toBeNull()
  expect(screen.queryByRole('heading', { name: 'Your reviews' })).toBeNull()
})

// Started at Stripe but not finished: the checklist still shows payouts to do, and the button continues.
test('payout setup that was started continues, and staff are told only the owner can', async () => {
  renderOverview({
    payouts: { connected: true, onboardingComplete: false },
    programs: [{ ...program, isPublished: false }],
    role: 'Staff',
    onboarding: new Response(null, { status: 403 }),
  })

  const checklist = (await screen.findByRole('heading', { name: 'Get started' })).closest('section') as HTMLElement
  expect(within(checklist).getAllByText('(done)')).toHaveLength(1)
  fireEvent.click(screen.getByRole('button', { name: 'Continue payout setup' }))

  expect(await screen.findByText('Only the owner can set up payouts.')).toBeInTheDocument()
})

// Selling: no checklist, the month's numbers and the best seller, and the newest reviews with the rating.
test('a selling workspace sees its numbers and newest reviews instead of the checklist', async () => {
  renderOverview({
    payouts: { connected: true, onboardingComplete: true },
    programs: [program],
    summary: {
      sales: 7,
      salesThisMonth: 3,
      earnings: 30870,
      earningsThisMonth: 13230,
      currency: 'EUR',
      customers: 6,
      bestSeller: 'Strong start',
      bestSellerSales: 5,
    },
    reviews: {
      items: [
        { id: 'r-1', programId: 'p-1', programTitle: 'Strong start', rating: 5, comment: 'Great plan', authorName: 'Maria K.', reply: null },
        { id: 'r-2', programId: 'p-1', programTitle: 'Strong start', rating: 4, comment: null, authorName: 'Nikos P.', reply: 'Thanks!' },
      ],
      page: 1,
      pageSize: 2,
      totalCount: 2,
      average: 4.5,
      reviewCount: 2,
    },
  })

  const numbers = (await screen.findByRole('heading', { name: 'Your numbers' })).closest('section') as HTMLElement
  expect(within(numbers).getByText('3')).toBeInTheDocument()
  expect(within(numbers).getByText(/132\.30/)).toBeInTheDocument()
  expect(within(numbers).getByText(/308\.70/)).toBeInTheDocument()
  expect(numbers).toHaveTextContent('Best seller: Strong start (5 sales)')
  const reviews = (await screen.findByRole('heading', { name: 'Your reviews' })).closest('section') as HTMLElement
  expect(reviews).toHaveTextContent('2 reviews')
  expect(within(reviews).getByText('No comment, just stars.')).toBeInTheDocument()
  expect(within(reviews).getAllByText('Not answered yet')).toHaveLength(1)
  expect(screen.queryByRole('heading', { name: 'Get started' })).toBeNull()
})
