import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { CoachTermsVersion } from '@/lib/legal'
import { signedInAs } from '@/test-auth'
import { CreateWorkspace } from './CreateWorkspace.tsx'

afterEach(() => vi.unstubAllGlobals())

function renderPage(answer: () => Response) {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      return answer()
    }),
  )
  const auth = signedInAs('coach')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={['/studio/new']}>
          <Routes>
            <Route path="/studio/new" element={<CreateWorkspace />} />
            <Route path="/studio/:slug" element={<p>Studio</p>} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

// Opening a workspace accepts the Terms for coaches (design §5.4): the version shown is sent.
test('a workspace opens only once the Terms for coaches are accepted, and sends their version', async () => {
  const requests = renderPage(() => Response.json({ id: 't1', slug: 'anna', displayName: 'Anna Coaching' }, { status: 201 }))
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Anna Coaching' } })
  const create = screen.getByRole('button', { name: 'Create workspace' })
  expect(create).toBeDisabled()
  expect(screen.getByRole('link', { name: 'Terms for coaches' })).toHaveAttribute('href', '/legal/coaches')

  fireEvent.click(screen.getByRole('checkbox', { name: /I accept the Terms for coaches/ }))
  fireEvent.click(create)

  expect(await screen.findByText('Studio')).toBeInTheDocument()
  await waitFor(() => expect(requests).toHaveLength(1))
  expect(await requests[0]!.json()).toEqual({ slug: 'anna-coaching', displayName: 'Anna Coaching', termsVersion: CoachTermsVersion })
})

test('changed terms ask the coach to reload rather than calling the address taken', async () => {
  renderPage(() => Response.json({ title: 'Read and accept the current Terms for coaches to open a workspace.', reason: 'terms' }, { status: 409 }))
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Anna Coaching' } })
  fireEvent.click(screen.getByRole('checkbox', { name: /I accept the Terms for coaches/ }))
  fireEvent.click(screen.getByRole('button', { name: 'Create workspace' }))

  expect(await screen.findByText(/The Terms for coaches have changed/)).toBeInTheDocument()
})
