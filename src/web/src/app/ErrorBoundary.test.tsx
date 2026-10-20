import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { ErrorBoundary } from './ErrorBoundary.tsx'

afterEach(() => vi.restoreAllMocks())

let broken = true
function Page() {
  if (broken) throw new Error('boom')
  return <p>Page content</p>
}

test('a crashed page shows a way out, and trying again renders it', () => {
  vi.spyOn(console, 'error').mockImplementation(() => {})
  render(
    <ErrorBoundary>
      <Page />
    </ErrorBoundary>,
  )

  expect(screen.getByRole('alert')).toHaveTextContent('Something went wrong')
  broken = false
  fireEvent.click(screen.getByRole('button', { name: /Try again/ }))
  expect(screen.getByText('Page content')).toBeInTheDocument()
})

test('a page whose code is gone after a release asks for a reload', () => {
  vi.spyOn(console, 'error').mockImplementation(() => {})
  function Stale(): never {
    throw new TypeError('Failed to fetch dynamically imported module: /assets/Cart-abc.js')
  }
  render(
    <ErrorBoundary>
      <Stale />
    </ErrorBoundary>,
  )

  expect(screen.getByRole('alert')).toHaveTextContent('A new version of Athlos is out')
  expect(screen.getByRole('button', { name: /Reload/ })).toBeInTheDocument()
})
