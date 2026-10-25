import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { CoachTermsVersion } from '@/lib/legal'
import { signedInAs } from '@/test-auth'
import { CoachTermsNotice } from './CoachTermsNotice.tsx'

afterEach(() => vi.unstubAllGlobals())

function renderNotice(role: 'Owner' | 'Staff', current: boolean) {
  const requests: Request[] = []
  let accepted = false
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      const path = new URL(request.url).pathname
      if (path === '/api/v1/me/memberships') return Response.json([{ tenantId: 't', slug: 'anna', displayName: 'Anna', role, status: 'Active' }])
      if (request.method === 'POST') {
        accepted = true
        return new Response(null, { status: 204 })
      }
      return Response.json({ id: 't', slug: 'anna', displayName: 'Anna', coachTermsVersion: null, coachTermsCurrent: current || accepted, branding: {} })
    }),
  )
  const auth = signedInAs('coach')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={['/studio/anna']}>
          <Routes>
            <Route path="/studio/:slug" element={<CoachTermsNotice />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

// A workspace that has not accepted the Terms for coaches in force (design §5.4): the owner accepts
// the version shown, and the notice goes.
test('the owner accepts the current Terms for coaches, and the notice goes', async () => {
  const requests = renderNotice('Owner', false)

  fireEvent.click(await screen.findByRole('checkbox', { name: /I accept the Terms for coaches/ }))
  fireEvent.click(screen.getByRole('button', { name: 'Accept' }))

  await waitFor(() => expect(screen.queryByText('New Terms for coaches')).toBeNull())
  const post = requests.find((r) => r.method === 'POST')!
  expect(new URL(post.url).pathname).toBe('/api/v1/studio/anna/workspace/coach-terms')
  expect(await post.json()).toEqual({ termsVersion: CoachTermsVersion })
})

test('staff are told the owner needs to accept; a workspace up to date sees nothing', async () => {
  renderNotice('Staff', false)
  expect(await screen.findByText(/The owner of this workspace needs to accept them/)).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Accept' })).toBeNull()
})

test('nothing is shown once the workspace accepted the current terms', async () => {
  const requests = renderNotice('Owner', true)
  await waitFor(() => expect(requests.some((r) => new URL(r.url).pathname === '/api/v1/studio/anna/workspace')).toBe(true))
  expect(screen.queryByText('New Terms for coaches')).toBeNull()
})
