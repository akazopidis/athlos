import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import type { components } from '@/api/ordering'
import { useApp } from './AppContext.tsx'
import { CartContext, CartStorageKey, MaxCartItems, type CartItem } from './cart.ts'

type Line = components['schemas']['MyCartLine']

// Holds the cart (see cart.ts) and keeps it in this browser's localStorage, in step across tabs. Signed
// in, the cart is also kept with the account (ordering-api's /api/v1/me/cart), so it follows the
// customer to another device: the first load merges what this browser held into the account's cart,
// and after that every change sends the whole cart (the last write wins).
function load(): CartItem[] {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(CartStorageKey) ?? '[]')
    return Array.isArray(stored) ? (stored as CartItem[]).filter((i) => typeof i?.programId === 'string').slice(0, MaxCartItems) : []
  } catch {
    return []
  }
}

const toItem = (l: Line): CartItem => ({
  programId: l.programId,
  coachSlug: l.coachSlug,
  slug: l.slug,
  title: l.title,
  priceAmount: Number(l.priceAmount),
  currency: l.currency,
  category: l.category ?? undefined,
})

const toLine = (i: CartItem): Line => ({
  programId: i.programId,
  coachSlug: i.coachSlug,
  slug: i.slug,
  title: i.title,
  priceAmount: Number(i.priceAmount),
  currency: i.currency,
  category: i.category ?? null,
})

// What the account holds is compared by program, in order: covers and prices may differ.
const sameCart = (a: CartItem[], b: CartItem[]) => a.map((i) => i.programId).join() === b.map((i) => i.programId).join()

export function CartProvider({ children }: { children: ReactNode }) {
  const { api, auth } = useApp()
  const signedIn = Boolean(auth?.account)
  const [items, setItems] = useState<CartItem[]>(load)
  // The account's cart as last read or written; null until it has been read (or when signed out).
  const account = useRef<CartItem[] | null>(null)

  useEffect(() => {
    try {
      localStorage.setItem(CartStorageKey, JSON.stringify(items))
    } catch {
      // Private windows and full storage: the cart still works for this visit.
    }
  }, [items])

  // Another tab changed the cart.
  useEffect(() => {
    const sync = (e: StorageEvent) => {
      if (e.key === CartStorageKey) setItems(load())
    }
    window.addEventListener('storage', sync)
    return () => window.removeEventListener('storage', sync)
  }, [])

  // Signed in: the account's cart, with anything this browser held that it lacks added after it.
  useEffect(() => {
    if (!signedIn) return
    let cancelled = false
    void api.GET('/api/v1/me/cart').then(({ data }) => {
      if (cancelled || !Array.isArray(data?.items)) return
      const saved = data.items.map(toItem)
      const extra = load().filter((i) => !saved.some((s) => s.programId === i.programId))
      account.current = saved
      setItems([...saved, ...extra].slice(0, MaxCartItems))
    })
    return () => {
      cancelled = true
    }
  }, [signedIn, api])

  // Every change after that goes to the account. A failed write leaves this browser's cart as it is.
  useEffect(() => {
    if (!signedIn || account.current === null || sameCart(account.current, items)) return
    account.current = items
    void api.PUT('/api/v1/me/cart', { body: { items: items.map(toLine) } }).catch(() => undefined)
  }, [signedIn, api, items])

  const has = useCallback((programId: string) => items.some((i) => i.programId === programId), [items])
  const add = useCallback(
    (item: CartItem) => {
      if (items.some((i) => i.programId === item.programId)) return true
      if (items.length >= MaxCartItems) return false
      setItems([...items, item])
      return true
    },
    [items],
  )
  const remove = useCallback((...programIds: string[]) => setItems((current) => current.filter((i) => !programIds.includes(i.programId))), [])
  const clear = useCallback(() => setItems([]), [])

  const cart = useMemo(() => ({ items, has, add, remove, clear }), [items, has, add, remove, clear])
  return <CartContext.Provider value={cart}>{children}</CartContext.Provider>
}
