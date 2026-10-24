import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import type { ReactElement } from 'react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { StudioDiscounts } from './StudioDiscounts.tsx'
import { StudioReviews } from './StudioReviews.tsx'

// The studio's Discounts and Reviews tabs (design §2.4).
afterEach(() => vi.unstubAllGlobals())

const signedIn = signedInAs('coach')

function renderTab(path: string, page: ReactElement, answer: (request: Request, path: string) => Response | undefined) {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      const url = new URL(request.url).pathname
      const answered = answer(request, url)
      if (answered) return answered
      if (url === '/api/v1/me/memberships') return Response.json([{ tenantId: 't', slug: 'anna', displayName: 'Anna', role: 'Owner', status: 'Active' }])
      if (url === '/api/v1/studio/anna/programs') return Response.json([{ id: 'p1', title: 'Strong start' }])
      return Response.json({ slug: 'anna', displayName: 'Anna' })
    }),
  )
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth: signedIn, api: createApi(signedIn) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={[`/studio/anna/${path}`]}>
          <Routes>
            <Route path={`/studio/:slug/${path}`} element={page} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('a coach creates a code for one program and sees its uses', async () => {
  const requests = renderTab('discounts', <StudioDiscounts />, (request, path) => {
    if (path === '/api/v1/studio/anna/discounts' && request.method === 'GET') {
      return Response.json([
        { id: 'd1', code: 'SPRING', percentOff: 15, programId: 'p1', expiresAt: null, maxRedemptions: 10, isActive: true, redemptions: 4, createdAt: '2026-10-01T00:00:00Z' },
      ])
    }
    if (request.method === 'POST') return Response.json({}, { status: 201 })
    return undefined
  })

  const code = await screen.findByText('SPRING')
  expect(code.closest('li')).toHaveTextContent('Strong start · 4 uses of 10')
  fireEvent.change(screen.getByLabelText('Code', { exact: true }), { target: { value: 'summer-20' } })
  fireEvent.change(screen.getByLabelText('Percent off'), { target: { value: '20' } })
  fireEvent.change(screen.getByLabelText('Applies to'), { target: { value: 'p1' } })
  fireEvent.change(screen.getByLabelText('Number of uses (optional)'), { target: { value: '50' } })
  fireEvent.click(screen.getByRole('button', { name: 'Create code' }))

  await waitFor(() => expect(requests.some((r) => r.method === 'POST')).toBe(true))
  const post = requests.find((r) => r.method === 'POST')!
  expect(await post.json()).toEqual({ code: 'summer-20', percentOff: 20, programId: 'p1', expiresAt: null, maxRedemptions: 50 })
})

test('a coach replies to a review publicly, and a hidden review is marked', async () => {
  const review = (id: string, hiddenAt: string | null) => ({
    id,
    programId: 'p1',
    programTitle: 'Strong start',
    rating: 4,
    comment: `Comment ${id}`,
    authorName: 'Maria K.',
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
    reply: null,
    repliedAt: null,
    hiddenAt,
  })
  const requests = renderTab('reviews', <StudioReviews />, (request, path) => {
    if (path === '/api/v1/studio/anna/reviews') return Response.json({ items: [review('r1', null), review('r2', '2026-10-02T00:00:00Z')], pageNumber: 1, pageSize: 20, totalCount: 2 })
    if (request.method === 'PUT') return new Response(null, { status: 204 })
    return undefined
  })

  const first = (await screen.findByText('Comment r1')).closest('li')!
  expect((await screen.findByText('Comment r2')).closest('li')).toHaveTextContent('Hidden by Athlos after a report')
  fireEvent.click(within(first).getByRole('button', { name: /^Reply$/ }))
  fireEvent.change(within(first).getByLabelText('Your public reply'), { target: { value: 'Thank you!' } })
  fireEvent.click(within(first).getByRole('button', { name: 'Post reply' }))

  await waitFor(() => expect(requests.some((r) => r.method === 'PUT')).toBe(true))
  const put = requests.find((r) => r.method === 'PUT')!
  expect(new URL(put.url).pathname).toBe('/api/v1/studio/anna/reviews/r1/reply')
  expect(await put.json()).toEqual({ text: 'Thank you!' })
})
