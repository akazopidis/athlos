import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { Studio } from './Studio.tsx'

afterEach(() => vi.unstubAllGlobals())

const workspace = {
  slug: 'anna',
  displayName: 'Anna Coaching',
  plan: 'Free',
  status: 'Active',
  branding: { primaryColor: '#123456' },
  coachTermsVersion: '2026-10-20',
  coachTermsCurrent: true,
}

// The studio's frame (design §8): the workspace header and tabs around the page in the outlet. The
// gateway answers 404 for a workspace the caller is not a member of.
function renderStudio(answer: Response, url = '/studio/anna/sales') {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname
      if (path === '/api/v1/studio/anna/workspace') return answer.clone()
      if (path === '/api/v1/me/memberships') return Response.json([{ slug: 'anna', role: 'Owner' }])
      return new Response(null, { status: 404 })
    }),
  )
  const auth = signedInAs('owner')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={[url]}>
          <Routes>
            <Route path="/studio/:slug" element={<Studio />}>
              <Route index element={<p>Overview page</p>} />
              <Route path="sales" element={<p>Sales page</p>} />
            </Route>
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
}

test('the studio shows the workspace, its plan and status, and the page of the open tab', async () => {
  renderStudio(Response.json(workspace))

  expect(await screen.findByRole('heading', { name: 'Anna Coaching' })).toBeInTheDocument()
  expect(screen.getByText('Free plan')).toBeInTheDocument()
  expect(screen.getByText('Active')).toBeInTheDocument()
  expect(screen.getByText('Sales page')).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Sales' })).toHaveAttribute('aria-current', 'page')
  expect(screen.getByRole('link', { name: 'Overview' })).not.toHaveAttribute('aria-current')
  expect(screen.getByRole('link', { name: /View storefront/ })).toHaveAttribute('href', '/c/anna')
  expect(document.title).toContain('Anna Coaching studio')
})

test('a workspace the caller does not coach in is not found, and no page opens', async () => {
  renderStudio(new Response(null, { status: 404 }))

  expect(await screen.findByText('Workspace not found')).toBeInTheDocument()
  expect(screen.queryByText('Sales page')).toBeNull()
})
