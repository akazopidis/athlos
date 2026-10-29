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

const ready = {
  id: 'e-1',
  kind: 'Workspace',
  workspace: 'anna',
  requestedAt: '2026-10-29T10:00:00Z',
  status: 'Ready',
  readyAt: '2026-10-29T10:00:05Z',
  expiresAt: '2026-11-05T10:00:05Z',
}

// The owner's workspace export (design §5.4) against a fake tenancy-api: asking queues it, and a ready
// one of this workspace is downloaded as a file. `exports` is what the list answers.
function renderCard(exports: object[], post: Response = Response.json({ ...ready, status: 'Preparing' }, { status: 202 })) {
  const requests: { method: string; path: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname
      requests.push({ method: request.method, path })
      if (request.method === 'POST') return post.clone()
      if (path === '/api/v1/me/exports') return Response.json(exports)
      return new Response('{"workspace":{}}', { headers: { 'Content-Type': 'application/json' } })
    }),
  )
  const auth = signedInAs('owner')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DownloadWorkspaceData slug="anna" />
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('asking queues the export, which is listed while it is prepared', async () => {
  const requests = renderCard([])

  fireEvent.click(screen.getByRole('button', { name: /Prepare workspace data/ }))

  await waitFor(() => expect(requests.some((r) => r.method === 'POST')).toBe(true))
  expect(requests.find((r) => r.method === 'POST')!.path).toBe('/api/v1/studio/anna/workspace/exports')
  // The list is read again after asking.
  await waitFor(() => expect(requests.filter((r) => r.path === '/api/v1/me/exports')).toHaveLength(2))
})

// Only this workspace's exports, and a ready one downloads as a file named after it.
test('a ready export of this workspace downloads as a file', async () => {
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:export'), revokeObjectURL: vi.fn() }))
  const saved: string[] = []
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    saved.push(this.download)
  })
  const requests = renderCard([ready, { ...ready, id: 'e-2', workspace: 'ben' }, { ...ready, id: 'e-3', kind: 'Account', workspace: null }])

  const download = await screen.findByRole('button', { name: /Download/ })
  expect(screen.getAllByRole('listitem')).toHaveLength(1)
  fireEvent.click(download)

  await waitFor(() => expect(saved).toEqual(['athlos-anna-2026-10-29.json']))
  expect(requests.some((r) => r.path === '/api/v1/me/exports/e-1/file')).toBe(true)
})

test('a refused request is explained', async () => {
  renderCard([], Response.json({ title: "Only the owner can download the workspace's data." }, { status: 403, headers: { 'Content-Type': 'application/problem+json' } }))

  fireEvent.click(screen.getByRole('button', { name: /Prepare workspace data/ }))

  expect(await screen.findByRole('alert')).toHaveTextContent('Only the owner')
})
