import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { ErrorBoundary } from './ErrorBoundary.tsx'

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

function captureBeacons() {
  const sent: { url: string; body: Blob }[] = []
  vi.stubGlobal('navigator', { ...navigator, sendBeacon: (url: string, body: Blob) => (sent.push({ url, body }), true) })
  return sent
}

let broken = true
function Page() {
  if (broken) throw new Error('boom')
  return <p>Page content</p>
}

test('a crashed page shows a way out, and trying again renders it', async () => {
  vi.spyOn(console, 'error').mockImplementation(() => {})
  const beacons = captureBeacons()
  render(
    <ErrorBoundary>
      <Page />
    </ErrorBoundary>,
  )

  expect(screen.getByRole('alert')).toHaveTextContent('Something went wrong')
  expect(beacons).toHaveLength(1)
  expect(beacons[0].url).toBe('/client-errors')
  expect(JSON.parse(await beacons[0].body.text())).toMatchObject({ message: 'boom', path: '/' })
  broken = false
  fireEvent.click(screen.getByRole('button', { name: /Try again/ }))
  expect(screen.getByText('Page content')).toBeInTheDocument()
})

test('a page whose code is gone after a release asks for a reload, and is not reported', () => {
  vi.spyOn(console, 'error').mockImplementation(() => {})
  const beacons = captureBeacons()
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
  expect(beacons).toHaveLength(0)
})
