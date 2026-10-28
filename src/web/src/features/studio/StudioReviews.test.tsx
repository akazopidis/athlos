import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { StudioReviews } from './StudioReviews.tsx'

afterEach(() => vi.unstubAllGlobals())

const review = (id: string, extra: object) => ({
  id,
  programId: '0199a000-0000-7000-8000-000000000001',
  programTitle: 'Strong start',
  authorName: 'Maria K.',
  rating: 5,
  comment: 'Great.',
  reply: null,
  hiddenAt: null,
  createdAt: '2026-10-20T00:00:00Z',
  ...extra,
})

// The coach's reviews (design §2.4) against a fake enrollment-api: requests are recorded.
function renderReviews(items: object[]) {
  const requests: { method: string; path: string; body: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname
      requests.push({ method: request.method, path, body: await request.clone().text() })
      if (path === '/api/v1/studio/anna/reviews') return Response.json({ items, page: 1, pageSize: 20, totalCount: items.length })
      if (path === '/api/v1/studio/anna/workspace') return Response.json({ slug: 'anna' })
      if (path === '/api/v1/me/memberships') return Response.json([{ slug: 'anna', role: 'Owner' }])
      return new Response(null, { status: 204 })
    }),
  )
  const auth = signedInAs('owner')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={['/studio/anna/reviews']}>
          <Routes>
            <Route path="/studio/:slug/reviews" element={<StudioReviews />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

const card = async (author: string) => (await screen.findByText(author)).closest('li') as HTMLElement

test('the coach replies to a review, and an empty reply cannot be posted', async () => {
  const requests = renderReviews([review('r-1', {})])

  fireEvent.click(within(await card('Maria K.')).getByRole('button', { name: /Reply/ }))
  const post = screen.getByRole('button', { name: 'Post reply' })
  expect(post).toBeDisabled()
  fireEvent.change(screen.getByLabelText('Your public reply'), { target: { value: 'Thank you, Maria!' } })
  fireEvent.click(post)

  await waitFor(() => expect(requests.some((r) => r.method === 'PUT')).toBe(true))
  const put = requests.find((r) => r.method === 'PUT')!
  expect(put.path).toBe('/api/v1/studio/anna/reviews/r-1/reply')
  expect(JSON.parse(put.body)).toEqual({ text: 'Thank you, Maria!' })
})

test('a reply can be removed, and a hidden review is marked and cannot be reported again', async () => {
  const requests = renderReviews([
    review('r-1', { reply: 'Thanks!' }),
    review('r-2', { authorName: 'Nikos P.', comment: null, hiddenAt: '2026-10-21T00:00:00Z' }),
  ])

  const hidden = await card('Nikos P.')
  expect(within(hidden).getByText(/Hidden by Athlos/)).toBeInTheDocument()
  expect(within(hidden).getByText('No comment, just stars.')).toBeInTheDocument()
  expect(within(hidden).queryByRole('button', { name: /Report/ })).toBeNull()
  fireEvent.click(within(await card('Maria K.')).getByRole('button', { name: 'Remove' }))

  await waitFor(() => expect(requests).toContainEqual(expect.objectContaining({ method: 'DELETE', path: '/api/v1/studio/anna/reviews/r-1/reply' })))
})
