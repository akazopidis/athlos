import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { ProgramReviews } from './ProgramReviews.tsx'

afterEach(() => vi.unstubAllGlobals())

const review = (id: string, extra: object = {}) => ({
  id,
  rating: 5,
  comment: `Comment ${id}`,
  authorName: `Author ${id}`,
  createdAt: '2026-10-20T10:00:00Z',
  reply: null,
  mine: false,
  ...extra,
})

// A program's public reviews (design §2.4) against a fake enrollment-api, paged by five.
function renderReviews(pages: Record<number, object>, signedIn = true) {
  const requests: { method: string; path: string; page: string | null; body: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const url = new URL(request.url)
      requests.push({ method: request.method, path: url.pathname, page: url.searchParams.get('Page'), body: await request.clone().text() })
      if (request.method === 'POST') return new Response(null, { status: 204 })
      return Response.json(pages[Number(url.searchParams.get('Page'))])
    }),
  )
  const auth = signedIn ? signedInAs('customer') : null
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ProgramReviews programId="p-1" coachName="Anna Coaching" />
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('the average, the share of each star count, and the coach replies', async () => {
  renderReviews({
    1: {
      average: 4.5,
      count: 4,
      stars: [0, 0, 1, 0, 3],
      totalCount: 4,
      items: [review('a', { reply: 'Thank you!' }), review('b', { rating: 3, comment: null }), review('c'), review('d')],
    },
  })

  expect(await screen.findByText('4 reviews')).toBeInTheDocument()
  const share = (stars: string) => within(screen.getByText(stars).parentElement as HTMLElement).getByText(/%$/).textContent
  expect([share('5 stars'), share('3 stars'), share('1 star')]).toEqual(['75%', '25%', '0%'])
  expect(screen.getByText('Anna Coaching replied')).toBeInTheDocument()
  expect(screen.getByText('Thank you!')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Show more reviews' })).toBeNull()
})

test('more reviews load five at a time', async () => {
  const requests = renderReviews({
    1: { average: 5, count: 7, stars: [0, 0, 0, 0, 7], totalCount: 7, items: ['1', '2', '3', '4', '5'].map((id) => review(id)) },
    2: { average: 5, count: 7, stars: [0, 0, 0, 0, 7], totalCount: 7, items: ['6', '7'].map((id) => review(id)) },
  })

  fireEvent.click(await screen.findByRole('button', { name: 'Show more reviews' }))

  expect(await screen.findByText('Comment 7')).toBeInTheDocument()
  expect(requests.map((r) => r.page)).toEqual(['1', '2'])
  expect(screen.queryByRole('button', { name: 'Show more reviews' })).toBeNull()
})

// Reporting: offered on others' reviews only, and only when signed in.
test('a signed-in customer reports another review, never their own', async () => {
  const requests = renderReviews({
    1: { average: 4, count: 2, stars: [0, 0, 0, 2, 0], totalCount: 2, items: [review('mine', { mine: true, rating: 4 }), review('other', { rating: 4 })] },
  })

  const reports = await screen.findAllByRole('button', { name: 'Report' })
  expect(reports).toHaveLength(1)
  fireEvent.click(reports[0]!)
  fireEvent.change(screen.getByRole('combobox'), { target: { value: 'Spam' } })
  fireEvent.click(screen.getByRole('button', { name: 'Send report' }))

  expect(await screen.findByRole('status')).toHaveTextContent('the Athlos team will take a look')
  const post = requests.find((r) => r.method === 'POST')!
  expect(post.path).toBe('/api/v1/marketplace/programs/p-1/reviews/other/report')
  expect(JSON.parse(post.body)).toEqual({ reason: 'Spam', note: null })
})

test('signed out, there is nothing to report with, and no reviews says so', async () => {
  renderReviews({ 1: { average: 0, count: 0, stars: [0, 0, 0, 0, 0], totalCount: 0, items: [] } }, false)

  expect(await screen.findByText(/No reviews yet/)).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Report' })).toBeNull()
})
