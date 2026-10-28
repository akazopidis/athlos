import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { signedInAs } from '@/test-auth'
import { MyReview } from './MyReview.tsx'

afterEach(() => vi.unstubAllGlobals())

const programId = '0199a000-0000-7000-8000-000000000001'
const saved = { rating: 4, comment: 'Solid plan.', reply: 'Thanks!', hiddenAt: null, createdAt: '2026-10-20T00:00:00Z', updatedAt: '2026-10-20T00:00:00Z' }

// The customer's review (design §2.4) against a fake enrollment-api: requests are recorded.
function renderReview(review: object | null, status = 204) {
  const requests: { method: string; body: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push({ method: request.method, body: await request.clone().text() })
      return status === 204 ? new Response(null, { status }) : Response.json({ title: 'Reviews close when a program is refunded.' }, { status })
    }),
  )
  const auth = signedInAs('customer')
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter>
          {/* @ts-expect-error: the test passes plain objects for the generated type */}
          <MyReview programId={programId} review={review} />
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('a first review needs stars, and an empty comment is sent as none', async () => {
  const requests = renderReview(null)

  const post = screen.getByRole('button', { name: 'Post review' })
  expect(post).toBeDisabled()
  fireEvent.click(screen.getByRole('radio', { name: '5 stars' }))
  fireEvent.change(screen.getByLabelText(/What did you think/), { target: { value: '   ' } })
  fireEvent.click(post)

  await waitFor(() => expect(requests).toHaveLength(1))
  expect(requests[0]).toEqual({ method: 'PUT', body: JSON.stringify({ rating: 5, comment: null }) })
})

test('a saved review shows the coach reply, and editing keeps what was there', async () => {
  const requests = renderReview(saved)

  expect(screen.getByRole('heading', { name: 'Your review' })).toBeInTheDocument()
  expect(screen.getByText('The coach replied')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: /Edit/ }))
  expect(screen.getByRole('radio', { name: '4 stars' })).toBeChecked()
  expect(screen.getByLabelText(/What did you think/)).toHaveValue('Solid plan.')
  fireEvent.click(screen.getByRole('radio', { name: '2 stars' }))
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

  await waitFor(() => expect(requests).toHaveLength(1))
  expect(JSON.parse(requests[0]!.body)).toEqual({ rating: 2, comment: 'Solid plan.' })
})

test('a review Athlos hid says so and can still be deleted', async () => {
  const requests = renderReview({ ...saved, hiddenAt: '2026-10-21T00:00:00Z' })

  expect(screen.getByText(/Athlos hid this review/)).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Delete review' }))

  await waitFor(() => expect(requests.map((r) => r.method)).toEqual(['DELETE']))
})

test('a refused save is explained', async () => {
  renderReview(null, 409)

  fireEvent.click(screen.getByRole('radio', { name: '3 stars' }))
  fireEvent.click(screen.getByRole('button', { name: 'Post review' }))

  expect(await screen.findByRole('alert')).toHaveTextContent('Reviews close when a program is refunded.')
})
