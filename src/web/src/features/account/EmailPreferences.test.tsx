import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { EmailPreferences } from './EmailPreferences.tsx'
import { Unsubscribe } from './Unsubscribe.tsx'

afterEach(() => {
  vi.unstubAllGlobals()
  window.history.replaceState(null, '', '/')
})

const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }

function renderWith(page: React.ReactNode, answer: (request: Request) => Response, signedIn = true) {
  const auth = signedIn ? signedInAs('customer', 'token') : null
  const requests: { method: string; path: string; body: unknown }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const body = request.method === 'GET' ? null : await request.clone().json()
      requests.push({ method: request.method, path: new URL(request.url).pathname, body })
      return answer(request)
    }),
  )
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>{page}</MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('each optional email is a toggle that saves at once', async () => {
  const requests = renderWith(<EmailPreferences />, (request) =>
    request.method === 'GET'
      ? Response.json({ programUpdates: true, reviewReplies: true })
      : Response.json({ programUpdates: false, reviewReplies: true }),
  )

  const workouts = await screen.findByRole('checkbox', { name: /New workouts/ })
  expect(workouts).toBeChecked()
  expect(screen.getByRole('checkbox', { name: /Replies to your reviews/ })).toBeChecked()
  fireEvent.click(workouts)

  await waitFor(() => expect(screen.getByRole('checkbox', { name: /New workouts/ })).not.toBeChecked())
  expect(requests.at(-1)).toEqual({ method: 'PUT', path: '/api/v1/me/email-preferences', body: { programUpdates: false, reviewReplies: true } })
})

test('a failed save says so and keeps the saved choice', async () => {
  renderWith(<EmailPreferences />, (request) =>
    request.method === 'GET' ? Response.json({ programUpdates: true, reviewReplies: true }) : new Response(null, { status: 500 }),
  )

  fireEvent.click(await screen.findByRole('checkbox', { name: /Replies to your reviews/ }))

  expect(await screen.findByRole('alert')).toHaveTextContent(/not saved/)
  expect(screen.getByRole('checkbox', { name: /Replies to your reviews/ })).toBeChecked()
})

test('the unsubscribe link posts its token only after a click, without sign-in', async () => {
  window.history.replaceState(null, '', '/unsubscribe#abc_123')
  const requests = renderWith(<Unsubscribe />, () => Response.json({ topic: 'programUpdates' }), false)

  expect(window.location.hash).toBe('')
  expect(requests).toHaveLength(0)
  fireEvent.click(screen.getByRole('button', { name: 'Unsubscribe' }))

  expect(await screen.findByText(/no longer get emails about new workouts/)).toBeInTheDocument()
  expect(requests).toEqual([{ method: 'POST', path: '/api/v1/email-preferences/unsubscribe', body: { token: 'abc_123' } }])
})

test('a link that no longer works points to the account settings', async () => {
  window.history.replaceState(null, '', '/unsubscribe#old')
  renderWith(<Unsubscribe />, () => new Response(null, { status: 404 }), false)

  fireEvent.click(screen.getByRole('button', { name: 'Unsubscribe' }))

  expect(await screen.findByRole('alert')).toHaveTextContent(/no longer works/)
  expect(screen.getByRole('link', { name: /account/ })).toHaveAttribute('href', '/me/account')
})
