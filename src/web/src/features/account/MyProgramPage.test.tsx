import type { PublicClientApplication } from '@azure/msal-browser'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { MyProgramPage } from './MyProgramPage.tsx'

afterEach(() => vi.unstubAllGlobals())

const programId = '0199a000-0000-7000-8000-000000000001'
const workout = (id: string, day: number, completedAt: string | null) => ({
  id,
  week: 1,
  day,
  title: `Day ${day}`,
  content: '5x5 squats',
  videoUrl: null,
  completedAt,
})

const signedIn = {
  getActiveAccount: () => ({ username: 'customer' }),
  acquireTokenSilent: async () => ({ accessToken: 'token' }),
} as unknown as PublicClientApplication

test('ticking a workout marks it done', async () => {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request)
      if (request.method === 'PUT') return new Response(null, { status: 204 })
      return Response.json({
        programId,
        coachSlug: 'anna',
        title: 'Strong start',
        description: 'Eight weeks of basics.',
        level: 'Beginner',
        durationWeeks: 8,
        coverImageUrl: null,
        enrolledAt: '2026-10-01T10:00:00Z',
        workouts: [workout('w1', 1, '2026-10-01T11:00:00Z'), workout('w2', 2, null)],
      })
    }),
  )

  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, msal: signedIn, api: createApi(signedIn, config.apiScope) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={[`/me/programs/${programId}`]}>
          <Routes>
            <Route path="/me/programs/:programId" element={<MyProgramPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )

  expect(await screen.findByText('Workouts (1 of 2 done)')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('checkbox', { name: /Day 2/ }))

  await waitFor(() => expect(requests.some((r) => r.method === 'PUT')).toBe(true))
  const put = requests.find((r) => r.method === 'PUT')!
  expect(new URL(put.url).pathname).toBe(`/api/v1/me/programs/${programId}/workouts/w2/completion`)
})
