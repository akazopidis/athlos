import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { StudioTeam } from './StudioTeam.tsx'

afterEach(() => vi.unstubAllGlobals())

const owner = { userId: 'u-owner', displayName: 'Anna', email: 'anna@coach.test', role: 'Owner', isYou: true, since: '2026-10-01T00:00:00Z' }
const staff = { userId: 'u-staff', displayName: 'Ben', email: 'ben@coach.test', role: 'Staff', isYou: false, since: '2026-10-02T00:00:00Z' }
const invite = { id: 'i-1', email: 'cleo@coach.test', expiresAt: '2026-11-04T00:00:00Z', expired: false }

// The team (design §5.2) against a fake tenancy-api: requests are recorded, answers come by route.
function renderTeam({ isOwner, members, answer }: { isOwner: boolean; members: object[]; answer?: (request: Request) => Response | undefined }) {
  const requests: { method: string; path: string; body: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname
      requests.push({ method: request.method, path, body: await request.clone().text() })
      const custom = answer?.(request)
      if (custom) return custom
      if (path === '/api/v1/studio/anna/members') return Response.json(members)
      if (path === '/api/v1/studio/anna/invites' && request.method === 'GET') return Response.json([invite])
      if (path === '/api/v1/studio/anna/invites') return Response.json({ ...invite, id: 'i-2' }, { status: 201 })
      return new Response(null, { status: 204 })
    }),
  )
  const auth = signedInAs(isOwner ? 'owner' : 'staff')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/studio/anna/settings']}>
          <Routes>
            <Route path="/studio/anna/settings" element={<StudioTeam slug="anna" isOwner={isOwner} />} />
            <Route path="/me/workspaces" element={<p>My workspaces</p>} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

const row = async (name: string) => (await screen.findByText(name)).closest('li') as HTMLElement

test('handing over ownership asks first, warns about payouts, then sends it', async () => {
  const requests = renderTeam({ isOwner: true, members: [owner, staff] })

  fireEvent.click(within(await row('Ben')).getByRole('button', { name: 'Make owner' }))

  expect(screen.getByText(/Your Stripe account is disconnected from it/)).toBeInTheDocument()
  expect(requests.some((r) => r.path.endsWith('/ownership'))).toBe(false)
  fireEvent.click(within(await row('Ben')).getByRole('button', { name: 'Make owner, become staff' }))

  await waitFor(() => expect(requests).toContainEqual(expect.objectContaining({ method: 'POST', path: '/api/v1/studio/anna/members/u-staff/ownership' })))
})

test('the owner can remove staff but has no way to leave, and cancelling sends nothing', async () => {
  const requests = renderTeam({ isOwner: true, members: [owner, staff] })

  expect(within(await row('Anna')).queryByRole('button')).toBeNull()
  fireEvent.click(within(await row('Ben')).getByRole('button', { name: 'Remove' }))
  fireEvent.click(within(await row('Ben')).getByRole('button', { name: 'Cancel' }))
  fireEvent.click(within(await row('Ben')).getByRole('button', { name: 'Remove' }))
  fireEvent.click(within(await row('Ben')).getByRole('button', { name: 'Remove' }))

  await waitFor(() => expect(requests.filter((r) => r.method === 'DELETE')).toEqual([expect.objectContaining({ path: '/api/v1/studio/anna/members/u-staff' })]))
})

test('a staff member sees no invites and leaving takes them to their workspaces', async () => {
  const requests = renderTeam({ isOwner: false, members: [{ ...owner, isYou: false, email: null }, { ...staff, isYou: true }] })

  expect(within(await row('Anna')).queryByRole('button')).toBeNull()
  expect(screen.queryByLabelText('Invite staff by email')).toBeNull()
  fireEvent.click(within(await row('Ben')).getByRole('button', { name: 'Leave' }))
  fireEvent.click(within(await row('Ben')).getByRole('button', { name: 'Leave workspace' }))

  expect(await screen.findByText('My workspaces')).toBeInTheDocument()
  expect(requests).toContainEqual(expect.objectContaining({ method: 'DELETE', path: '/api/v1/studio/anna/members/u-staff' }))
  expect(requests.some((r) => r.path.endsWith('/invites'))).toBe(false)
})

test('the owner invites an address and revokes an open invite', async () => {
  const requests = renderTeam({ isOwner: true, members: [owner] })

  fireEvent.change(await screen.findByLabelText('Invite staff by email'), { target: { value: '  dora@coach.test ' } })
  fireEvent.click(screen.getByRole('button', { name: 'Send invite' }))
  expect(await screen.findByText('Invite sent.')).toBeInTheDocument()
  fireEvent.click(within(await row('cleo@coach.test')).getByRole('button', { name: 'Revoke' }))

  await waitFor(() => expect(requests).toContainEqual(expect.objectContaining({ method: 'DELETE', path: '/api/v1/studio/anna/invites/i-1' })))
  expect(JSON.parse(requests.find((r) => r.method === 'POST')!.body)).toEqual({ email: 'dora@coach.test' })
})

test('a refused invite is explained', async () => {
  renderTeam({
    isOwner: true,
    members: [owner],
    answer: (request) =>
      request.method === 'POST' ? Response.json({ title: 'That address is already a member.' }, { status: 409 }) : undefined,
  })

  fireEvent.change(await screen.findByLabelText('Invite staff by email'), { target: { value: 'anna@coach.test' } })
  fireEvent.click(screen.getByRole('button', { name: 'Send invite' }))

  expect(await screen.findByText('That address is already a member.')).toBeInTheDocument()
})
