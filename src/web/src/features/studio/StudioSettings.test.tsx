import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { StudioSettings } from './StudioSettings.tsx'

afterEach(() => vi.unstubAllGlobals())

const workspace = {
  slug: 'anna',
  displayName: 'Anna Coaching',
  branding: {
    primaryColor: '#123456',
    logoUrl: null,
    uploadedLogoUrl: null,
    contactEmail: 'hello@anna.test',
    bio: 'I coach runners.',
    instagramUrl: 'https://instagram.com/anna',
    youTubeUrl: null,
    websiteUrl: null,
  },
}

// Workspace settings (design §8) against a fake tenancy-api. The studio shell has loaded the workspace
// before a page opens, so the cache starts with it here too.
function renderSettings(role: 'Owner' | 'Staff', answer?: (request: Request) => Response | undefined) {
  const requests: { method: string; path: string; body: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname
      requests.push({ method: request.method, path, body: await request.clone().text() })
      const custom = answer?.(request)
      if (custom) return custom
      if (path === '/api/v1/studio/anna/workspace' && request.method === 'GET') return Response.json(workspace)
      if (path === '/api/v1/me/memberships') return Response.json([{ slug: 'anna', role }])
      if (path === '/api/v1/studio/anna/members') return Response.json([])
      if (path === '/api/v1/studio/anna/invites') return Response.json([])
      return new Response(null, { status: 204 })
    }),
  )
  const auth = signedInAs('owner')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  queryClient.setQueryData(['workspace', 'anna'], workspace)
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/studio/anna/settings']}>
          <Routes>
            <Route path="/studio/:slug/settings" element={<StudioSettings />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('the owner saves the storefront: trimmed values, and an emptied field is removed', async () => {
  const requests = renderSettings('Owner')

  await screen.findByRole('button', { name: 'Save' })
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: '  Anna Strong  ' } })
  fireEvent.change(screen.getByLabelText('Instagram'), { target: { value: '' } })
  fireEvent.change(screen.getByLabelText('Website'), { target: { value: 'https://anna.test' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  expect(await screen.findByText('Saved.')).toBeInTheDocument()
  const put = requests.find((r) => r.method === 'PUT' && r.path === '/api/v1/studio/anna/workspace')!
  expect(JSON.parse(put.body)).toEqual({
    displayName: 'Anna Strong',
    logoUrl: null,
    primaryColor: '#123456',
    contactEmail: 'hello@anna.test',
    bio: 'I coach runners.',
    instagramUrl: null,
    youTubeUrl: null,
    websiteUrl: 'https://anna.test',
  })
  // The preview follows the form before it is saved.
  expect(screen.getAllByText('Anna Strong').length).toBeGreaterThan(0)
})

test('a refused save shows why', async () => {
  renderSettings('Owner', (request) =>
    request.method === 'PUT'
      ? Response.json({ title: 'Links must start with https://', status: 400 }, { status: 400, headers: { 'Content-Type': 'application/problem+json' } })
      : undefined,
  )

  fireEvent.click(await screen.findByRole('button', { name: 'Save' }))

  expect(await screen.findByText('Links must start with https://')).toBeInTheDocument()
  expect(screen.queryByText('Saved.')).toBeNull()
})

// Staff see the settings but cannot change them, and the owner's sections (logo, data, deletion) are not there.
test('staff see the settings read-only', async () => {
  const requests = renderSettings('Staff')

  await waitFor(() => expect(requests.some((r) => r.path === '/api/v1/me/memberships')).toBe(true))
  expect(await screen.findByText('Only the owner can change these.')).toBeInTheDocument()
  expect(screen.getByLabelText('Name')).toBeDisabled()
  expect(screen.queryByRole('button', { name: 'Save' })).toBeNull()
  expect(screen.queryByText('Logo or photo')).toBeNull()
  expect(screen.queryByText('Your data')).toBeNull()
})
