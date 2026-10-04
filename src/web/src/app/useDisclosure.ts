import { useEffect, useState, type RefObject } from 'react'
import { useLocation } from 'react-router'

// A show/hide panel (the mobile menu, the account menu) without a UI library: it closes on Escape,
// on a click outside the element (header or menu) holding it, and when the page changes.
export function useDisclosure(ref: RefObject<HTMLElement | null>) {
  const [open, setOpen] = useState(false)
  const { pathname } = useLocation()
  const [openedOn, setOpenedOn] = useState(pathname)
  if (open && openedOn !== pathname) setOpen(false)

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    const onPointer = (e: PointerEvent) => !ref.current?.contains(e.target as Node) && setOpen(false)
    document.addEventListener('keydown', onKey)
    document.addEventListener('pointerdown', onPointer)
    return () => {
      document.removeEventListener('keydown', onKey)
      document.removeEventListener('pointerdown', onPointer)
    }
  }, [open, ref])

  const toggle = () => {
    setOpenedOn(pathname)
    setOpen((o) => !o)
  }
  return { open, toggle, close: () => setOpen(false) }
}
