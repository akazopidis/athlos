import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { Footer } from './Footer.tsx'

afterEach(() => vi.unstubAllGlobals())

test('shows the version reported by the gateway', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json({ version: 'v0.1.0' })))

  render(
    <QueryClientProvider client={new QueryClient()}>
      <Footer />
    </QueryClientProvider>,
  )

  expect(await screen.findByText('Athlos v0.1.0')).toBeInTheDocument()
})
