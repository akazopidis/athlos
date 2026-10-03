import type { PublicClientApplication } from '@azure/msal-browser'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '../api/client.ts'
import { AppContext } from '../AppContext.tsx'
import { AcceptInvite } from './AcceptInvite.tsx'

afterEach(() => {
  vi.unstubAllGlobals()
  sessionStorage.clear()
  window.history.replaceState(null, '', '/')
})

const signedIn = {
  getActiveAccount: () => ({ username: 'invitee' }),
  acquireTokenSilent: async () => ({ accessToken: 'token' }),
} as unknown as PublicClientApplication

test('accepting sends the token from the link fragment and removes it from the address bar', async () => {
  window.history.replaceState(null, '', '/invites/anna#secret-token')
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      return Response.json({ slug: 'anna', displayName: 'Anna Coaching' })
    }),
  )

  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, msal: signedIn, api: createApi(signedIn, config.apiScope) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={['/invites/anna']}>
          <Routes>
            <Route path="/invites/:slug" element={<AcceptInvite />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )

  expect(window.location.hash).toBe('')
  fireEvent.click(screen.getByRole('button', { name: 'Accept invite' }))

  expect(await screen.findByText('Welcome to Anna Coaching')).toBeInTheDocument()
  await waitFor(() => expect(requests).toHaveLength(1))
  expect(new URL(requests[0].url).pathname).toBe('/api/v1/invites/anna/accept')
  expect(await requests[0].json()).toEqual({ token: 'secret-token' })
  expect(sessionStorage.getItem('athlos.invite-token')).toBeNull()
})
