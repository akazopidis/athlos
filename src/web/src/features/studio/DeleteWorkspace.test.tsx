import type { PublicClientApplication } from '@azure/msal-browser'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { DeleteWorkspace } from './DeleteWorkspace.tsx'

afterEach(() => vi.unstubAllGlobals())

const signedIn = {
  getActiveAccount: () => ({ username: 'owner' }),
  acquireTokenSilent: async () => ({ accessToken: 'token' }),
} as unknown as PublicClientApplication

test('the owner must type the address, then the workspace is deleted and they land on My account', async () => {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      return new Response(null, { status: 204 })
    }),
  )

  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, msal: signedIn, api: createApi(signedIn, config.apiScope) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={['/studio/anna/settings']}>
          <Routes>
            <Route path="/studio/anna/settings" element={<DeleteWorkspace slug="anna" />} />
            <Route path="/me" element={<p>My account page</p>} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )

  const button = screen.getByRole('button', { name: /Delete workspace/ })
  fireEvent.change(screen.getByLabelText(/to confirm/), { target: { value: 'ann' } })
  expect(button).toBeDisabled()
  fireEvent.change(screen.getByLabelText(/to confirm/), { target: { value: 'anna' } })
  fireEvent.click(button)

  expect(await screen.findByText('My account page')).toBeInTheDocument()
  await waitFor(() => expect(requests.some((r) => r.method === 'DELETE')).toBe(true))
  const deletion = requests.find((r) => r.method === 'DELETE')!
  expect(new URL(deletion.url).pathname).toBe('/api/v1/studio/anna/workspace')
  expect(await deletion.json()).toEqual({ confirmSlug: 'anna' })
})
