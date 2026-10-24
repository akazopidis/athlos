import { act, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { createApi } from '@/api/client'
import { signedInAs } from '@/test-auth'
import { AppContext } from './AppContext.tsx'
import { CartStorageKey, useCart, type Cart, type CartItem } from './cart.ts'
import { CartProvider } from './CartProvider.tsx'

afterEach(() => {
  vi.unstubAllGlobals()
  localStorage.clear()
})

const item = (programId: string, title: string): CartItem => ({ programId, coachSlug: 'anna', slug: title.toLowerCase(), title, priceAmount: 4900, currency: 'EUR' })
const line = (i: CartItem) => ({ ...i, category: null })

// Renders the provider and hands the test the cart, as a page would use it.
function renderCart(signedIn: boolean, account: CartItem[] = []) {
  const requests: Request[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request.clone())
      return Response.json({ items: request.method === 'GET' ? account.map(line) : [], updatedAt: null })
    }),
  )
  const auth = signedIn ? signedInAs('customer') : null
  const config = { entraClientId: 'client', entraAuthority: 'https://login.example', apiScope: 'api://athlos/access', version: 'test' }
  const cart: { current: Cart | null } = { current: null }
  function Probe() {
    cart.current = useCart()
    return <p>{cart.current.items.map((i) => i.title).join(', ') || 'empty'}</p>
  }
  render(
    <AppContext.Provider value={{ config, auth, api: createApi(auth) }}>
      <CartProvider>
        <Probe />
      </CartProvider>
    </AppContext.Provider>,
  )
  return { requests, cart }
}

test("signing in brings the account's cart, with what this browser held added after it", async () => {
  localStorage.setItem(CartStorageKey, JSON.stringify([item('p2', 'Engine'), item('p1', 'Strong')]))
  const { requests } = renderCart(true, [item('p1', 'Strong')])

  expect(await screen.findByText('Strong, Engine')).toBeInTheDocument()
  await waitFor(() => expect(requests.some((r) => r.method === 'PUT')).toBe(true))
  const put = requests.find((r) => r.method === 'PUT')!
  expect((await put.json()).items.map((i: CartItem) => i.programId)).toEqual(['p1', 'p2'])
})

test('a change while signed in is sent to the account; nothing is sent when the account already has it', async () => {
  const { requests, cart } = renderCart(true, [item('p1', 'Strong')])
  await screen.findByText('Strong')
  expect(requests.filter((r) => r.method === 'PUT')).toHaveLength(0)

  act(() => cart.current!.remove('p1'))

  await waitFor(() => expect(requests.filter((r) => r.method === 'PUT')).toHaveLength(1))
  expect((await requests.find((r) => r.method === 'PUT')!.json()).items).toEqual([])
})

test('signed out, the cart stays in this browser and nothing is sent', async () => {
  const { requests, cart } = renderCart(false)

  act(() => void cart.current!.add(item('p1', 'Strong')))

  expect(await screen.findByText('Strong')).toBeInTheDocument()
  expect(JSON.parse(localStorage.getItem(CartStorageKey)!)).toHaveLength(1)
  expect(requests).toHaveLength(0)
})
