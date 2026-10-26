import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { ReportContent } from './report-content.tsx'

afterEach(() => vi.unstubAllGlobals())

const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }

function renderReport(signedIn: boolean, target: Parameters<typeof ReportContent>[0]['target']) {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request)
      return new Response(null, { status: 204 })
    }),
  )
  const auth = signedIn ? signedInAs('customer') : null
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient()}>
        <ReportContent target={target} label="Report this program" />
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('a signed-in visitor reports a program with a reason and a note', async () => {
  const requests = renderReport(true, { kind: 'Program', programId: 'p1' })

  fireEvent.click(screen.getByRole('button', { name: 'Report this program' }))
  fireEvent.change(screen.getByLabelText('Why?'), { target: { value: 'Copyright' } })
  fireEvent.change(screen.getByLabelText('Note (optional)'), { target: { value: ' Copied from my book ' } })
  fireEvent.click(screen.getByRole('button', { name: 'Send report' }))

  expect(await screen.findByRole('status')).toHaveTextContent('Thanks, the Athlos team will take a look.')
  await waitFor(() => expect(requests).toHaveLength(1))
  expect(new URL(requests[0].url).pathname).toBe('/api/v1/me/reports')
  expect(await requests[0].json()).toEqual({ kind: 'Program', programId: 'p1', coachSlug: null, reason: 'Copyright', note: 'Copied from my book' })
})

test('signed out, reporting is not offered', () => {
  renderReport(false, { kind: 'Coach', coachSlug: 'anna' })

  expect(screen.queryByRole('button', { name: 'Report this program' })).not.toBeInTheDocument()
})
