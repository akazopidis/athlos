import { createContext, useContext } from 'react'

// The shopping cart (design §2.2, §8): programs from any coach, paid together in one Stripe
// checkout. It lives in this browser, and with the account once signed in (CartProvider). The prices
// here are what the visitor saw; checkout asks catalog again and charges the current price.
export interface CartItem {
  programId: string
  coachSlug: string
  slug: string
  title: string
  priceAmount: number
  currency: string
  coverImageUrl?: string | null
  // For the generated cover of a program without an image; items added before v1.12.0 have none.
  category?: string
}

// The same limit as ordering-api's checkout.
export const MaxCartItems = 10

export interface Cart {
  items: CartItem[]
  has: (programId: string) => boolean
  add: (item: CartItem) => boolean
  remove: (...programIds: string[]) => void
  clear: () => void
}

export const CartStorageKey = 'athlos.cart'
export const CartContext = createContext<Cart | null>(null)

export function useCart(): Cart {
  const cart = useContext(CartContext)
  if (!cart) throw new Error('useCart must be used inside CartProvider')
  return cart
}

// Totals per currency: checkout takes one currency at a time.
export function cartTotals(items: CartItem[]): [currency: string, amount: number][] {
  const totals = new Map<string, number>()
  for (const i of items) totals.set(i.currency, (totals.get(i.currency) ?? 0) + Number(i.priceAmount))
  return [...totals]
}
