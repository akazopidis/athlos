import type { PublicClientApplication } from '@azure/msal-browser'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { appRolesOf } from '@/lib/usePlatformAdmin'
import { Admin } from './Admin.tsx'

afterEach(() => vi.unstubAllGlobals())

// An unsigned token with the given claims; the page only reads them to decide what to show.
const tokenWith = (claims: object) => `e30.${btoa(JSON.stringify(claims)).replace(/=+$/, '')}.sig`

const workspace = { id: 'w1', slug: 'anna', displayName: 'Anna Coaching', status: 'Active', plan: 'Free', readyToSell: true, createdAt: '2026-10-01T00:00:00Z' }
const metrics = { workspaces: 3, active: 2, suspended: 1, deleted: 0, activeFree: 1, activePro: 1, readyToSell: 1, newLast30Days: 3 }

function renderAs(claims: object) {
  const msal = {
    getActiveAccount: () => ({ username: 'admin', localAccountId: 'admin' }),
    acquireTokenSilent: async () => ({ accessToken: tokenWith(claims) }),
  } as unknown as PublicClientApplication
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request)
      const path = new URL(request.url).pathname
      if (path === '/api/v1/admin/metrics') return Response.json(metrics)
      if (path === '/api/v1/admin/tenants') return Response.json({ items: [workspace], page: 1, pageSize: 20, totalCount: 1 })
      return Response.json({ ...workspace, status: 'Suspended' })
    }),
  )
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, msal, api: createApi(msal, config.apiScope) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter>
          <Admin />
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('roles come from the token, and anything unreadable means none', () => {
  expect(appRolesOf(tokenWith({ roles: ['Platform.Admin'] }))).toEqual(['Platform.Admin'])
  expect(appRolesOf(tokenWith({ scp: 'access_as_user' }))).toEqual([])
  expect(appRolesOf('not-a-token')).toEqual([])
})

test('a coach or customer sees that the page is for admins only', async () => {
  const requests = renderAs({ scp: 'access_as_user' })

  expect(await screen.findByText('Platform admins only')).toBeInTheDocument()
  expect(requests.some((r) => r.url.includes('/api/v1/admin/'))).toBe(false)
})

test('the admin sees the counts and suspends a workspace', async () => {
  const requests = renderAs({ roles: ['Platform.Admin'] })

  expect(await screen.findByText('Anna Coaching')).toBeInTheDocument()
  expect(screen.getByText('1 Free · 1 Pro')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Suspend' }))

  await waitFor(() => expect(requests.some((r) => r.method === 'PUT')).toBe(true))
  const put = requests.find((r) => r.method === 'PUT')!
  expect(new URL(put.url).pathname).toBe('/api/v1/admin/tenants/w1/status')
  expect(await put.json()).toEqual({ status: 'Suspended' })
})
