import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { CartContext, CartStorageKey, MaxCartItems, type CartItem } from './cart.ts'

// Holds the cart (see cart.ts) and keeps it in this browser's localStorage, in step across tabs.
function load(): CartItem[] {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(CartStorageKey) ?? '[]')
    return Array.isArray(stored) ? (stored as CartItem[]).filter((i) => typeof i?.programId === 'string').slice(0, MaxCartItems) : []
  } catch {
    return []
  }
}

export function CartProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<CartItem[]>(load)

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
