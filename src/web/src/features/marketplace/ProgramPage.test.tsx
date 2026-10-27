import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { CartProvider } from '@/lib/CartProvider'
import { TermsVersion } from '@/lib/legal'
import { leaveTo } from '@/lib/navigation'
import { signedInAs } from '@/test-auth'
import { ProgramPage } from './ProgramPage.tsx'

vi.mock('@/lib/navigation', () => ({ leaveTo: vi.fn() }))

afterEach(() => {
  discount = null
  vi.unstubAllGlobals()
  vi.mocked(leaveTo).mockReset()
  localStorage.clear()
})

const reviews = {
  average: 4.5,
  count: 2,
  stars: [0, 0, 0, 1, 1],
  items: [
    { id: 'r1', rating: 5, comment: 'Best squat cues I have had.', authorName: 'Maria K.', createdAt: '2026-10-01T10:00:00Z', updatedAt: '2026-10-01T10:00:00Z', reply: 'Thank you, Maria!', repliedAt: '2026-10-02T10:00:00Z' },
    { id: 'r2', rating: 4, comment: null, authorName: 'Nikos P.', createdAt: '2026-09-30T10:00:00Z', updatedAt: '2026-09-30T10:00:00Z', reply: null, repliedAt: null },
  ],
  page: 1,
  pageSize: 5,
  totalCount: 2,
}

const program = {
  id: '0199a000-0000-7000-8000-000000000001',
  coachSlug: 'anna',
  slug: 'strong-start',
  title: 'Strong start',
  description: 'Eight weeks of basics.',
  category: 'Powerlifting',
  durationWeeks: 8,
  priceAmount: 4900,
  currency: 'EUR',
  coverImageUrl: null,
  ratingAverage: 4.5,
  ratingCount: 2,
  outline: [
    { week: 1, day: 1, title: 'Squat day', sample: true },
    { week: 1, day: 2, title: 'Bench day', sample: false },
    { week: 2, day: 1, title: 'Deadlift day', sample: false },
  ],
  sample: { week: 1, day: 1, title: 'Squat day', content: 'Five sets of five squats.', videoUrl: null },
}

// The code the coach shows on the program (design §2.4), if a test sets one.
let discount: { code: string; percentOff: number; expiresAt: string | null } | null = null

// A signed-in customer.
const signedIn = signedInAs('customer')

function renderPage(checkout: (request: Request) => Response, owned: string[] = [], workspaces: string[] = []) {
  const requests: Request[] = []
  let saved: (typeof program)[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      if (request.method === 'POST') return checkout(request)
      if (new URL(request.url).pathname === '/api/v1/marketplace/coaches/anna') {
        return Response.json({ slug: 'anna', displayName: 'Anna Coaching', branding: { logoUrl: null, primaryColor: '#1d4ed8' } })
      }
      if (new URL(request.url).pathname === '/api/v1/me/programs') {
        return Response.json({ items: owned.map((programId) => ({ programId })), page: 1, pageSize: 20, totalCount: owned.length })
      }
      if (new URL(request.url).pathname === '/api/v1/marketplace/programs') {
        return Response.json({ items: [], page: 1, pageSize: 5, totalCount: 0, coaches: [] })
      }
      if (new URL(request.url).pathname === '/api/v1/marketplace/coaches') return Response.json([])
      if (new URL(request.url).pathname === `/api/v1/marketplace/programs/${program.id}/reviews`) return Response.json(reviews)
      if (new URL(request.url).pathname === '/api/v1/me/saved') return Response.json(saved)
      if (request.method === 'PUT' || request.method === 'DELETE') {
        saved = request.method === 'PUT' ? [program] : []
        return new Response(null, { status: 204 })
      }
      if (new URL(request.url).pathname === '/api/v1/me/memberships') {
        return Response.json(workspaces.map((slug) => ({ tenantId: 't', slug, displayName: slug, role: 'Owner', status: 'Active' })))
      }
      return Response.json({ ...program, discount })
    }),
  )

  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth: signedIn, api: createApi(signedIn) }}>
      <QueryClientProvider client={new QueryClient()}>
        <CartProvider>
          <MemoryRouter initialEntries={['/c/anna/programs/strong-start']}>
            <Routes>
              <Route path="/c/:slug/programs/:programSlug" element={<ProgramPage />} />
            </Routes>
          </MemoryRouter>
        </CartProvider>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return requests
}

test('Buy now starts a checkout of this program alone and leaves for Stripe', async () => {
  const requests = renderPage(() => Response.json({ checkoutId: 'checkout', checkoutUrl: 'https://checkout.stripe.test/s' }))

  expect(await screen.findByText('€49.00')).toBeInTheDocument()
  // The coach's name, from the storefront, links back to it (the breadcrumb and the byline).
  for (const link of await screen.findAllByRole('link', { name: 'Anna Coaching' })) expect(link).toHaveAttribute('href', '/c/anna')
  fireEvent.click(screen.getByRole('button', { name: 'Buy now' }))

  await waitFor(() => expect(leaveTo).toHaveBeenCalledWith('https://checkout.stripe.test/s'))
  const checkout = requests.find((r) => r.method === 'POST')!
  expect(new URL(checkout.url).pathname).toBe('/api/v1/orders/checkout')
  expect(await checkout.json()).toEqual({ programIds: [program.id], termsVersion: TermsVersion, immediateAccess: true })
  expect(checkout.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/)
  expect(checkout.headers.get('Authorization')).toBe('Bearer token')
})

test('shows why the server refused the checkout', async () => {
  renderPage(() =>
    Response.json({ title: 'You already own this program.', status: 409 }, { status: 409, headers: { 'Content-Type': 'application/problem+json' } }),
  )

  fireEvent.click(await screen.findByRole('button', { name: 'Buy now' }))

  expect(await screen.findByRole('alert')).toHaveTextContent('You already own this program.')
  expect(leaveTo).not.toHaveBeenCalled()
})

test('a customer who already owns the program gets Open program instead of Buy', async () => {
  renderPage(() => Response.json({}), [program.id])

  expect(await screen.findByRole('link', { name: 'Open program' })).toHaveAttribute('href', `/me/programs/${program.id}`)
  expect(screen.queryByRole('button', { name: 'Buy now' })).toBeNull()
  expect(screen.queryByRole('button', { name: /Add to cart/ })).toBeNull()
})

test('the code the coach shows is on the page, with the price it gives', async () => {
  discount = { code: 'SUMMER', percentOff: 20, expiresAt: null }
  renderPage(() => Response.json({}))

  const code = await screen.findByText('SUMMER')
  expect(code.closest('p')).toHaveTextContent(/20% off with code SUMMER: €39\.20/)
})

test("a coach sees their own workspace's program as theirs, not for sale", async () => {
  renderPage(() => Response.json({}), [], ['anna'])

  expect(await screen.findByRole('link', { name: 'Open in studio' })).toHaveAttribute('href', '/studio/anna')
  expect(screen.queryByRole('button', { name: /Add to cart/ })).toBeNull()
  expect(screen.queryByRole('button', { name: 'Buy now' })).toBeNull()
})

test('Add to cart keeps the program in the cart, in this browser', async () => {
  renderPage(() => Response.json({}))

  fireEvent.click(await screen.findByRole('button', { name: /Add to cart/ }))

  expect(await screen.findByRole('link', { name: /In cart/ })).toHaveAttribute('href', '/cart')
  expect(JSON.parse(localStorage.getItem('athlos.cart')!)).toEqual([expect.objectContaining({ programId: program.id, title: 'Strong start', priceAmount: 4900 })])
})

test('the page shows the outline by week and the free preview in full', async () => {
  renderPage(() => Response.json({}))

  const outline = await screen.findByRole('region', { name: "What's inside" })
  expect(outline).toHaveTextContent('3 workouts over 8 weeks')
  expect(outline).toHaveTextContent('Week 2')
  expect(outline).toHaveTextContent('Deadlift day')
  expect(screen.getByRole('region', { name: 'Week 1, day 1: Squat day' })).toHaveTextContent('Five sets of five squats.')
})

test('Save keeps the program for later', async () => {
  const requests = renderPage(() => Response.json({}))

  fireEvent.click(await screen.findByRole('button', { name: 'Save Strong start for later' }))

  expect(await screen.findByRole('button', { name: 'Remove Strong start from saved' })).toHaveAttribute('aria-pressed', 'true')
  expect(requests.some((r) => r.method === 'PUT' && new URL(r.url).pathname === `/api/v1/me/saved/${program.id}`)).toBe(true)
})

test('the page shows the rating and the reviews with the coach replies', async () => {
  renderPage(() => Response.json({}))

  expect(await screen.findByRole('link', { name: /4\.5 out of 5 stars.*2 reviews/ })).toHaveAttribute('href', '#reviews')
  const section = await screen.findByRole('region', { name: 'Reviews' })
  expect(section).toHaveTextContent('Best squat cues I have had.')
  expect(section).toHaveTextContent('Anna Coaching replied')
  expect(section).toHaveTextContent('Thank you, Maria!')
  expect(screen.getAllByRole('img', { name: '5.0 out of 5 stars' }).length).toBeGreaterThan(0)
})

test('a signed-in visitor reports a review to the Athlos team', async () => {
  const requests = renderPage(() => new Response(null, { status: 204 }))

  const section = await screen.findByRole('region', { name: 'Reviews' })
  fireEvent.click(within(section).getAllByRole('button', { name: 'Report' })[0]!)
  fireEvent.change(screen.getByLabelText('Why?'), { target: { value: 'OffTopic' } })
  fireEvent.click(screen.getByRole('button', { name: 'Send report' }))

  expect(await screen.findByText('Thanks, the Athlos team will take a look.')).toBeInTheDocument()
  const post = requests.find((r) => r.method === 'POST')!
  expect(new URL(post.url).pathname).toBe(`/api/v1/marketplace/programs/${program.id}/reviews/r1/report`)
  expect(await post.json()).toEqual({ reason: 'OffTopic', note: null })
})
