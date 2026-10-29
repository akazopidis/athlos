import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { appRolesOf } from '@/lib/usePlatformAdmin'
import { signedInAs } from '@/test-auth'
import { Admin } from './Admin.tsx'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

// An unsigned token with the given claims; the page only reads them to decide what to show.
const tokenWith = (claims: object) => `e30.${btoa(JSON.stringify(claims)).replace(/=+$/, '')}.sig`

const workspace = { id: 'w1', slug: 'anna', displayName: 'Anna Coaching', status: 'Active', plan: 'Free', readyToSell: true, createdAt: '2026-10-01T00:00:00Z' }
const metrics = { workspaces: 3, active: 2, suspended: 1, deleted: 0, activeFree: 1, activePro: 1, readyToSell: 1, newLast30Days: 3 }
const action = (id: string, kind: string, subject: string, at: string) => ({ id, adminId: '0199aaaa-0000-7000-8000-000000000000', action: kind, subject, detail: null, reason: `Reason ${id}`, at })
const logs: Record<string, object[]> = {
  '/api/v1/admin/actions': [action('a1', 'WorkspaceSuspended', 'Ben Runs (/c/ben)', '2026-10-20T10:00:00Z')],
  '/api/v1/admin/reviews/actions': [action('a2', 'ReviewHidden', 'Strong start (/c/anna)', '2026-10-22T10:00:00Z')],
  '/api/v1/admin/payouts/actions': [],
}

function renderAs(claims: object, items: object[] = [workspace]) {
  const auth = signedInAs('admin', tokenWith(claims))
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request)
      const path = new URL(request.url).pathname
      if (path === '/api/v1/admin/metrics') return Response.json(metrics)
      if (path === '/api/v1/admin/tenants') return Response.json({ items, page: 1, pageSize: 20, totalCount: items.length })
      if (path in logs) return Response.json(logs[path])
      if (path === '/api/v1/admin/payouts' || path === '/api/v1/admin/reviews/reports') return Response.json([])
      return Response.json({ ...workspace, status: 'Suspended' })
    }),
  )
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
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

test('the admin sees the counts and suspends a workspace, giving a reason', async () => {
  const requests = renderAs({ roles: ['Platform.Admin'] })

  expect(await screen.findByText('Anna Coaching')).toBeInTheDocument()
  expect(screen.getByText('1 Free · 1 Pro')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Suspend' })).toBeDisabled()
  fireEvent.change(screen.getByLabelText('Reason for your next action'), { target: { value: '  Sells copied programs  ' } })
  fireEvent.click(screen.getByRole('button', { name: 'Suspend' }))

  await waitFor(() => expect(requests.some((r) => r.method === 'PUT')).toBe(true))
  const put = requests.find((r) => r.method === 'PUT')!
  expect(new URL(put.url).pathname).toBe('/api/v1/admin/tenants/w1/status')
  expect(await put.json()).toEqual({ status: 'Suspended', reason: 'Sells copied programs' })
  await waitFor(() => expect(screen.getByLabelText('Reason for your next action')).toHaveValue(''))
})

// Deleting (design §3.4): after a confirmation, the same job as the owner's, with the reason.
test('the admin deletes a workspace after confirming, giving a reason', async () => {
  const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
  const requests = renderAs({ roles: ['Platform.Admin'] })

  fireEvent.change(await screen.findByLabelText('Reason for your next action'), { target: { value: 'The owner asked by email' } })
  fireEvent.click(await screen.findByRole('button', { name: 'Delete Anna Coaching' }))

  await waitFor(() => expect(requests.some((r) => r.method === 'POST')).toBe(true))
  const post = requests.find((r) => r.method === 'POST')!
  expect(confirm).toHaveBeenCalled()
  expect(new URL(post.url).pathname).toBe('/api/v1/admin/tenants/w1/deletion')
  expect(await post.json()).toEqual({ reason: 'The owner asked by email' })
})

// A deletion its retries are still finishing: marked, with no other action offered.
test('a workspace being deleted offers no actions', async () => {
  renderAs({ roles: ['Platform.Admin'] }, [{ ...workspace, status: 'Suspended', deletionPending: true, lastDeletionError: 'No answer' }])

  expect(await screen.findByText('being deleted')).toHaveAttribute('title', 'No answer')
  expect(screen.queryByRole('button', { name: 'Reactivate' })).toBeNull()
  expect(screen.queryByRole('button', { name: /Delete/ })).toBeNull()
  expect(screen.getByLabelText('Plan of Anna Coaching')).toBeDisabled()
})

test('the log shows the admin actions of every service together, newest first', async () => {
  renderAs({ roles: ['Platform.Admin'] })

  const entries = await screen.findAllByText(/^Reason a\d$/)
  expect(entries.map((e) => e.textContent)).toEqual(['Reason a2', 'Reason a1'])
  expect(screen.getByText('Review hidden')).toBeInTheDocument()
  expect(screen.getByText('Ben Runs (/c/ben)')).toBeInTheDocument()
})
