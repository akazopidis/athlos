import type { PublicClientApplication } from '@azure/msal-browser'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { StudioProgramEditor } from './StudioProgramEditor.tsx'

afterEach(() => vi.unstubAllGlobals())

const signedIn = {
  getActiveAccount: () => ({ username: 'coach' }),
  acquireTokenSilent: async () => ({ accessToken: 'token' }),
} as unknown as PublicClientApplication

const programId = '0199a000-0000-7000-8000-000000000001'

function renderEditor(program: Record<string, unknown>) {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      const path = new URL(request.url).pathname
      if (path.endsWith('/workouts')) return Response.json([])
      if (path.endsWith('/memberships')) return Response.json([])
      if (path.endsWith('/workspace')) return Response.json({ slug: 'anna' })
      return Response.json(program)
    }),
  )

  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, msal: signedIn, api: createApi(signedIn, config.apiScope) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={[`/studio/anna/programs/${programId}`]}>
          <Routes>
            <Route path="/studio/:slug/programs/:programId" element={<StudioProgramEditor />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

const draft = {
  id: programId,
  slug: 'strong-start',
  title: 'Strong start',
  description: 'Basics',
  level: 'Beginner',
  durationWeeks: 4,
  priceAmount: 1900,
  currency: 'EUR',
  isPublished: false,
  publishedAt: null,
  updatedAt: '2026-10-03T10:00:00Z',
  coverImageUrl: null,
  everPublished: false,
}

test('uploading a cover sends the raw image with its content type', async () => {
  const requests = renderEditor(draft)
  const file = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], 'cover.png', { type: 'image/png' })

  fireEvent.change(await screen.findByLabelText('Cover image file'), { target: { files: [file] } })

  await waitFor(() => expect(requests.some((r) => r.method === 'PUT')).toBe(true))
  const upload = requests.find((r) => r.method === 'PUT')!
  expect(new URL(upload.url).pathname).toBe(`/api/v1/studio/anna/programs/${programId}/cover`)
  expect(upload.headers.get('Content-Type')).toBe('image/png')
  expect(new Uint8Array(await upload.arrayBuffer())).toEqual(new Uint8Array([0x89, 0x50, 0x4e, 0x47]))
})

test('a program that was on sale offers no delete, a draft does', async () => {
  renderEditor({ ...draft, isPublished: false, everPublished: true })
  expect(await screen.findByText(/it cannot be deleted/)).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /Delete program/ })).toBeNull()
})

test('a draft that was never on sale can be deleted', async () => {
  renderEditor(draft)
  expect(await screen.findByRole('button', { name: /Delete program/ })).toBeInTheDocument()
})
