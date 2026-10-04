import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { AppContext } from '@/lib/AppContext'
import { Home } from './Home.tsx'

afterEach(() => vi.unstubAllGlobals())

const program = (n: number) => ({
  coachSlug: 'anna',
  slug: `program-${n}`,
  title: `Program ${n}`,
  level: 'Advanced',
  durationWeeks: 8,
  priceAmount: 4900,
  currency: 'EUR',
  coverImageUrl: null,
})

test('the search in the URL reaches the API and Next loads the following page', async () => {
  const searches: URLSearchParams[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const url = new URL(request.url)
      searches.push(url.searchParams)
      const page = Number(url.searchParams.get('Page'))
      return Response.json({ items: [program(page)], page, pageSize: 12, totalCount: 13 })
    }),
  )

  const config = { entraClientId: null, entraAuthority: null, apiScope: null, version: 'test' }
  render(
    <AppContext.Provider value={{ config, msal: null, api: createApi(null, null) }}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={['/?q=strength&level=Advanced']}>
          <Routes>
            <Route path="/" element={<Home />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </AppContext.Provider>,
  )

  expect(await screen.findByText('Program 1')).toBeInTheDocument()
  expect(searches[0].get('Q')).toBe('strength')
  expect(searches[0].get('Level')).toBe('Advanced')
  expect(searches[0].get('PageSize')).toBe('12')
  expect(screen.getByText('Page 1 of 2')).toBeInTheDocument()

  fireEvent.click(screen.getByRole('button', { name: /Next/ }))

  expect(await screen.findByText('Program 2')).toBeInTheDocument()
  await waitFor(() => expect(searches.at(-1)?.get('Page')).toBe('2'))
  expect(searches.at(-1)?.get('Q')).toBe('strength')
})
