import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import type { Auth } from '@/lib/auth'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { AccountMenu } from './AccountMenu.tsx'

afterEach(() => vi.unstubAllGlobals())

// An access token whose payload carries these app roles (the menu only reads the claim).
const tokenWith = (roles: string[]) => `header.${btoa(JSON.stringify({ roles })).replace(/=+$/, '')}.signature`

// The account menu (design §8) against a fake tenancy-api: requests are recorded.
function renderMenu(auth: Auth | null, memberships: object[] = []) {
  const requests: { method: string; path: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname
      requests.push({ method: request.method, path })
      if (path === '/api/v1/me/memberships') return Response.json(memberships)
      return Response.json({})
    }),
  )
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <AccountMenu />
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('signed out, the menu is a sign-in button', () => {
  const signIn = vi.fn(async () => {})
  renderMenu({ ...signedInAs('nobody'), account: null, signIn })

  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }))

  expect(signIn).toHaveBeenCalled()
})

// Signed in: the profile is kept in step with the token once, the studios of active workspaces are
// listed with the role, and signing out is the last entry.
test('the menu lists the account pages and the active workspaces, and signs out', async () => {
  const signOut = vi.fn(async () => {})
  const requests = renderMenu({ ...signedInAs('maria@example.com'), account: { username: 'maria@example.com', localAccountId: 'u-1', name: 'Maria Kosta' }, signOut }, [
    { tenantId: 't-1', slug: 'anna', displayName: 'Anna Coaching', role: 'Staff', status: 'Active' },
    { tenantId: 't-2', slug: 'gone', displayName: 'Gone Gym', role: 'Owner', status: 'Suspended' },
  ])

  fireEvent.click(screen.getByRole('button', { name: 'Account' }))

  expect(screen.getByText('MK')).toBeInTheDocument()
  expect(await screen.findByRole('link', { name: /Anna Coaching/ })).toHaveAttribute('href', '/studio/anna')
  expect(screen.queryByText('Gone Gym')).toBeNull()
  expect(screen.getByRole('link', { name: 'All workspaces' })).toHaveAttribute('href', '/me/workspaces')
  expect(screen.getByRole('link', { name: 'Account settings' })).toHaveAttribute('href', '/me/account')
  expect(screen.queryByRole('link', { name: 'Platform admin' })).toBeNull()
  expect(requests.filter((r) => r.method === 'PUT' && r.path === '/api/v1/me/profile')).toHaveLength(1)
  fireEvent.click(screen.getByRole('button', { name: /Sign out/ }))
  expect(signOut).toHaveBeenCalled()
})

test('a platform admin also gets the admin entry, and a user without workspaces is offered one', async () => {
  renderMenu(signedInAs('admin', tokenWith(['Platform.Admin'])))

  fireEvent.click(screen.getByRole('button', { name: 'Account' }))

  expect(await screen.findByRole('link', { name: 'Platform admin' })).toHaveAttribute('href', '/admin')
  expect(screen.getByRole('link', { name: 'Open a workspace' })).toHaveAttribute('href', '/me/workspaces')
})
