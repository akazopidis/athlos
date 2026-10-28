import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { CartProvider } from '@/lib/CartProvider'
import { Catalog } from './Catalog.tsx'
import type { ProgramSearch } from './useProgramSearch.ts'

afterEach(() => vi.unstubAllGlobals())

const program = {
  id: '0199a000-0000-7000-8000-000000000001',
  coachSlug: 'anna',
  slug: 'strong-start',
  title: 'Strong start',
  category: 'Running',
  durationWeeks: 8,
  priceAmount: 4900,
  currency: 'EUR',
  coverImageUrl: null,
}

// The catalog (design §8) with a search given as props: what reaches catalog-api and what the
// controls ask the page to change. The page itself keeps the search in the URL (Home.test.tsx).
function renderCatalog(search: ProgramSearch, { fixedCoach, items = [program] }: { fixedCoach?: string; items?: object[] } = {}) {
  const searches: URLSearchParams[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const url = new URL(request.url)
      if (url.pathname === '/api/v1/marketplace/coaches') return Response.json([{ slug: 'anna', displayName: 'Anna Coaching', branding: {} }])
      searches.push(url.searchParams)
      return Response.json({ items, page: 1, pageSize: 12, totalCount: items.length, coaches: [{ coachSlug: 'anna', count: items.length }] })
    }),
  )
  const update = vi.fn()
  const clear = vi.fn()
  const config = { entraClientId: null, entraAuthority: null, apiScope: null, version: 'test' }
  render(
    <AppContext.Provider value={{ config, auth: null, api: createApi(null) }}>
      <QueryClientProvider client={new QueryClient()}>
        <CartProvider>
          <MemoryRouter>
            <Catalog search={search} update={update} clear={clear} fixedCoach={fixedCoach} />
          </MemoryRouter>
        </CartProvider>
      </QueryClientProvider>
    </AppContext.Provider>,
  )
  return { searches, update, clear }
}

test('prices go to the API in cents and a length as a range of weeks', async () => {
  const { searches } = renderCatalog({ minPrice: 20, maxPrice: 60, length: 'medium', sort: 'PriceLowToHigh', coach: 'ben' })

  expect(await screen.findByText('Strong start')).toBeInTheDocument()
  const query = searches[0]!
  expect([query.get('MinPrice'), query.get('MaxPrice')]).toEqual(['2000', '6000'])
  expect([query.get('MinWeeks'), query.get('MaxWeeks')]).toEqual(['5', '8'])
  expect([query.get('Sort'), query.get('Coach'), query.get('Q')]).toEqual(['PriceLowToHigh', 'ben', null])
})

test('on a storefront the coach is fixed and offered as no filter', async () => {
  const { searches } = renderCatalog({ coach: 'ben' }, { fixedCoach: 'anna' })

  expect(await screen.findByText('Strong start')).toBeInTheDocument()
  expect(searches[0]!.get('Coach')).toBe('anna')
  expect(screen.queryByRole('radio', { name: 'All coaches' })).toBeNull()
})

test('the controls ask for their change: sort, price range and category', async () => {
  const { update } = renderCatalog({ sort: 'TopRated' })
  await screen.findByText('Strong start')

  fireEvent.change(screen.getByRole('combobox', { name: 'Sort by' }), { target: { value: 'Newest' } })
  fireEvent.change(screen.getByLabelText('Lowest price'), { target: { value: '15' } })
  fireEvent.change(screen.getByLabelText('Highest price'), { target: { value: '' } })
  fireEvent.click(screen.getByRole('button', { name: 'Go' }))
  fireEvent.click(screen.getByRole('radio', { name: 'Running' }))

  expect(update.mock.calls).toEqual([[{ sort: null }], [{ minPrice: 15, maxPrice: null }], [{ category: 'Running' }]])
})

// With a search, the best match comes first unless another sort is chosen (design §8); without one,
// "Best match" is not offered.
test('a search sorts by best match unless the shopper picks another order', async () => {
  const { searches, update } = renderCatalog({ q: 'yoga' })
  await screen.findByText('Strong start')
  const sort = screen.getByRole('combobox', { name: 'Sort by' })

  expect(sort).toHaveValue('Relevance')
  expect(searches[0]!.get('Sort')).toBeNull()
  fireEvent.change(sort, { target: { value: 'Newest' } })
  fireEvent.change(sort, { target: { value: 'Relevance' } })
  expect(update.mock.calls).toEqual([[{ sort: 'Newest' }], [{ sort: null }]])
})

test('without a search there is no best match to sort by', async () => {
  renderCatalog({})
  await screen.findByText('Strong start')

  expect(screen.getByRole('combobox', { name: 'Sort by' })).toHaveValue('Newest')
  expect(screen.queryByRole('option', { name: 'Best match' })).toBeNull()
})

test('no match offers to clear the filters', async () => {
  const { clear } = renderCatalog({ category: 'Mobility', maxPrice: 10 }, { items: [] })

  expect(await screen.findByText('No programs match')).toBeInTheDocument()
  expect(screen.getByText('0 programs')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }))

  await waitFor(() => expect(clear).toHaveBeenCalled())
})
