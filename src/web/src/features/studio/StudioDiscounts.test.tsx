import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { StudioDiscounts } from './StudioDiscounts.tsx'

afterEach(() => vi.unstubAllGlobals())

const day = 24 * 60 * 60 * 1000
const code = (id: string, extra: object) => ({
  id,
  code: id.toUpperCase(),
  percentOff: 20,
  programId: null,
  expiresAt: null,
  maxRedemptions: null,
  redemptions: 0,
  isActive: true,
  isPublic: false,
  createdAt: '2026-10-01T00:00:00Z',
  ...extra,
})
const programs = [{ id: 'p-1', title: 'Strong start' }]

// Discount codes (design §2.4) against a fake ordering-api: requests are recorded, answers by route.
function renderDiscounts(codes: object[], answer?: (request: Request) => Response | undefined) {
  const requests: { method: string; path: string; body: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname
      requests.push({ method: request.method, path, body: await request.clone().text() })
      const custom = answer?.(request)
      if (custom) return custom
      if (path === '/api/v1/studio/anna/discounts' && request.method === 'GET') return Response.json(codes)
      if (path === '/api/v1/studio/anna/programs') return Response.json(programs)
      if (path === '/api/v1/studio/anna/workspace') return Response.json({ slug: 'anna' })
      if (path === '/api/v1/me/memberships') return Response.json([{ slug: 'anna', role: 'Owner' }])
      return new Response(null, { status: request.method === 'POST' ? 201 : 204 })
    }),
  )
  const auth = signedInAs('owner')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/studio/anna/discounts']}>
          <Routes>
            <Route path="/studio/:slug/discounts" element={<StudioDiscounts />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

const row = async (name: string) => (await screen.findByText(name)).closest('li') as HTMLElement

test('each code says whether it is off, ended or used up, and only an open-ended live code can be shown', async () => {
  renderDiscounts([
    code('live', { programId: 'p-1', isPublic: true }),
    code('off', { isActive: false }),
    code('ended', { expiresAt: new Date(Date.now() - day).toISOString() }),
    code('usedup', { maxRedemptions: 5, redemptions: 5 }),
  ])

  const live = await row('LIVE')
  expect(within(live).getByText('Shown in the shop')).toBeInTheDocument()
  expect(within(live).getByText(/Strong start · 0 uses/)).toBeInTheDocument()
  expect(within(live).getByRole('button', { name: 'Stop showing' })).toBeInTheDocument()
  expect(within(await row('OFF')).getByText('Off')).toBeInTheDocument()
  expect(within(await row('ENDED')).getByText('Ended')).toBeInTheDocument()
  const usedUp = await row('USEDUP')
  expect(within(usedUp).getByText('Used up')).toBeInTheDocument()
  expect(within(usedUp).getByText(/All programs · 5 uses of 5/)).toBeInTheDocument()
  expect(within(usedUp).queryByRole('button', { name: 'Show in the shop' })).toBeNull()
})

test('switching a code off sends the rest of it unchanged', async () => {
  const requests = renderDiscounts([code('spring', { programId: 'p-1', percentOff: 15, maxRedemptions: 10, redemptions: 2 })])

  fireEvent.click(within(await row('SPRING')).getByRole('button', { name: 'Switch off' }))

  await waitFor(() => expect(requests.some((r) => r.method === 'PUT')).toBe(true))
  const put = requests.find((r) => r.method === 'PUT')!
  expect(put.path).toBe('/api/v1/studio/anna/discounts/spring')
  expect(JSON.parse(put.body)).toEqual({ percentOff: 15, programId: 'p-1', expiresAt: null, maxRedemptions: 10, isActive: false, isPublic: false })
})

test('a new code with a use limit cannot be shown in the shop', async () => {
  const requests = renderDiscounts([])

  expect(await screen.findByText('No codes yet')).toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Code'), { target: { value: 'summer-20' } })
  fireEvent.change(screen.getByLabelText('Percent off'), { target: { value: '25' } })
  fireEvent.change(screen.getByLabelText('Applies to'), { target: { value: 'p-1' } })
  fireEvent.click(screen.getByRole('checkbox', { name: /Show in the shop/ }))
  fireEvent.change(screen.getByLabelText('Number of uses (optional)'), { target: { value: '50' } })

  expect(screen.getByRole('checkbox', { name: /Show in the shop/ })).toBeDisabled()
  expect(screen.getByText('Only a code without a use limit can be shown.')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Create code' }))

  await waitFor(() => expect(requests.some((r) => r.method === 'POST')).toBe(true))
  expect(JSON.parse(requests.find((r) => r.method === 'POST')!.body)).toEqual({
    code: 'summer-20',
    percentOff: 25,
    programId: 'p-1',
    expiresAt: null,
    maxRedemptions: 50,
    isPublic: false,
  })
})

test('a refused code is explained', async () => {
  renderDiscounts([], (request) =>
    request.method === 'POST' ? Response.json({ title: 'You already have a code SUMMER.' }, { status: 409 }) : undefined,
  )

  fireEvent.change(await screen.findByLabelText('Code'), { target: { value: 'summer' } })
  fireEvent.click(screen.getByRole('button', { name: 'Create code' }))

  expect(await screen.findByRole('alert')).toHaveTextContent('You already have a code SUMMER.')
})
