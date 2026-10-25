import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { DownloadWorkspaceData } from './DownloadWorkspaceData.tsx'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function renderButton(answer: Response) {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      return answer.clone()
    }),
  )
  const auth = signedInAs('owner')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient()}>
        <DownloadWorkspaceData slug="anna" />
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

// The owner's workspace export (design §5.4) is saved by the browser as one JSON file.
test('the workspace data is saved as a file named after the workspace', async () => {
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:export'), revokeObjectURL: vi.fn() }))
  const saved: string[] = []
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    saved.push(this.download)
  })
  const requests = renderButton(Response.json({ workspace: { slug: 'anna' }, team: [], programs: {}, sales: {}, customers: {} }))

  fireEvent.click(screen.getByRole('button', { name: /Download workspace data/ }))

  await waitFor(() => expect(saved).toHaveLength(1))
  expect(new URL(requests[0]!.url).pathname).toBe('/api/v1/studio/anna/workspace/export')
  expect(saved[0]).toMatch(/^athlos-anna-\d{4}-\d{2}-\d{2}\.json$/)
})

test('a service that cannot answer is explained', async () => {
  renderButton(Response.json({ title: "The workspace's data could not be gathered just now. Try again in a moment." }, { status: 502 }))

  fireEvent.click(screen.getByRole('button', { name: /Download workspace data/ }))

  expect(await screen.findByRole('alert')).toHaveTextContent('could not be gathered')
})
