import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { MyWorkspaces } from './MyWorkspaces.tsx'

afterEach(() => vi.unstubAllGlobals())

// The user's workspaces (design §8, "/me/workspaces") against a fake tenancy-api.
function renderWorkspaces(memberships: object[]) {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json(memberships)))
  const auth = signedInAs('coach')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <MyWorkspaces />
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
}

// A suspended workspace's studio is closed (design §3.4): it is listed, but not as a link.
test('each workspace opens its studio with the role, except a suspended one', async () => {
  renderWorkspaces([
    { tenantId: 't-1', slug: 'anna', displayName: 'Anna Coaching', role: 'Owner', status: 'Active' },
    { tenantId: 't-2', slug: 'ben', displayName: 'Ben Runs', role: 'Staff', status: 'Active' },
    { tenantId: 't-3', slug: 'gone', displayName: 'Gone Gym', role: 'Owner', status: 'Suspended' },
    { tenantId: 't-4', slug: 'closing', displayName: 'Closing Club', role: 'Owner', status: 'Suspended', deletionPending: true },
  ])

  expect(await screen.findByRole('link', { name: /Anna Coaching.*Owner/ })).toHaveAttribute('href', '/studio/anna')
  expect(screen.getByRole('link', { name: /Ben Runs.*Staff/ })).toHaveAttribute('href', '/studio/ben')
  expect(screen.getByText('Gone Gym').closest('a')).toBeNull()
  expect(screen.getByText(/Suspended by Athlos/)).toBeInTheDocument()
  // A deletion still finishing (design §3.4) says so instead.
  expect(screen.getByText('Closing Club').closest('a')).toBeNull()
  expect(screen.getByText(/Being deleted/)).toBeInTheDocument()
})

test('without a workspace the page offers to open one', async () => {
  renderWorkspaces([])

  expect(await screen.findByText(/You don't coach in a workspace yet/)).toBeInTheDocument()
  expect(screen.getByRole('link', { name: /Open a workspace/ })).toHaveAttribute('href', '/studio/new')
})
