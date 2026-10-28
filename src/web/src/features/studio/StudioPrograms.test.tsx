import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { StudioPrograms } from './StudioPrograms.tsx'

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

// The coach's programs (design §8) against a fake catalog-api and the plans list.
function renderPrograms(programs: object[], answer?: (request: Request) => Response | undefined) {
  const requests: { method: string; path: string; body: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname
      requests.push({ method: request.method, path, body: await request.clone().text() })
      const custom = answer?.(request)
      if (custom) return custom
      if (path === '/api/v1/plans') return Response.json([{ name: 'Free', maxPrograms: 3 }, { name: 'Pro', maxPrograms: 50 }])
      if (request.method === 'GET') return Response.json(programs)
      return Response.json({ ...program, id: 'p-new', isPublished: false }, { status: 201 })
    }),
  )
  const auth = signedInAs('owner')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={['/studio/anna']}>
          <Routes>
            <Route path="/studio/anna" element={<StudioPrograms slug="anna" plan="Free" />} />
            <Route path="/studio/anna/programs/:id" element={<p>Editor</p>} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('the list shows price, category, length, state and the plan quota', async () => {
  renderPrograms([program, { ...program, id: 'p-2', title: 'Base', isPublished: false }])

  expect(await screen.findByText('2 of 3 programs · Free plan')).toBeInTheDocument()
  expect(screen.getAllByText(/€49\.00 · Powerlifting · 8 weeks/)).toHaveLength(2)
  expect(screen.getAllByText('Published').length).toBeGreaterThan(0)
  expect(screen.getAllByText('Draft').length).toBeGreaterThan(0)
})

test('a new program is sent in cents with a trimmed title and opens its editor', async () => {
  const requests = renderPrograms([])

  expect(await screen.findByText('No programs yet')).toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Title'), { target: { value: '  Strong start ' } })
  fireEvent.change(screen.getByLabelText('Address'), { target: { value: 'strong-start' } })
  fireEvent.change(screen.getByLabelText('Price (EUR)'), { target: { value: '24.99' } })
  fireEvent.submit(screen.getByLabelText('Title').closest('form')!)

  expect(await screen.findByText('Editor')).toBeInTheDocument()
  const post = JSON.parse(requests.find((r) => r.method === 'POST')!.body)
  expect(post).toMatchObject({ slug: 'strong-start', title: 'Strong start', category: 'GeneralFitness', durationWeeks: 4, priceAmount: 2499, currency: 'EUR' })
})

test('a refused program is explained', async () => {
  renderPrograms([], (request) =>
    request.method === 'POST' ? Response.json({ title: 'Your plan allows 3 programs.' }, { status: 409 }) : undefined,
  )

  fireEvent.change(await screen.findByLabelText('Title'), { target: { value: 'One more' } })
  fireEvent.change(screen.getByLabelText('Address'), { target: { value: 'one-more' } })
  fireEvent.submit(screen.getByLabelText('Title').closest('form')!)

  await waitFor(() => expect(screen.getByText('Your plan allows 3 programs.')).toBeInTheDocument())
})
