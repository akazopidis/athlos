import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { DeleteAccount } from './DeleteAccount.tsx'

afterEach(() => vi.unstubAllGlobals())

function renderWith(answer: Response) {
  const signOut = vi.fn()
  const auth = signedInAs('customer', 'token', { signOut })
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request)
      return answer.clone()
    }),
  )
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient()}>
        <DeleteAccount />
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return { requests, signOut }
}

test('the customer types the confirmation, the account is deleted and they are signed out', async () => {
  const { requests, signOut } = renderWith(new Response(null, { status: 204 }))

  const button = screen.getByRole('button', { name: /Delete account/ })
  expect(button).toBeDisabled()
  fireEvent.change(screen.getByLabelText(/to confirm/), { target: { value: 'delete my account' } })
  fireEvent.click(button)

  await waitFor(() => expect(signOut).toHaveBeenCalled())
  expect(requests[0].method).toBe('DELETE')
  expect(new URL(requests[0].url).pathname).toBe('/api/v1/me')
})

test('an owner is told which workspaces to delete first', async () => {
  const { signOut } = renderWith(
    Response.json({ title: 'Delete the workspaces you own first: anna.', status: 409 }, { status: 409, headers: { 'Content-Type': 'application/problem+json' } }),
  )

  fireEvent.change(screen.getByLabelText(/to confirm/), { target: { value: 'delete my account' } })
  fireEvent.click(screen.getByRole('button', { name: /Delete account/ }))

  expect(await screen.findByRole('alert')).toHaveTextContent('Delete the workspaces you own first: anna.')
  expect(signOut).not.toHaveBeenCalled()
})
