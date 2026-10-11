import { useEffect, useState } from 'react'

// True once `busy` has lasted longer than `afterMs`: services scale to zero (design §9), so the first
// request after a quiet spell can take a minute, and the page should say so instead of just spinning.
export function useSlow(busy: boolean, afterMs = 6000): boolean {
  const [slow, setSlow] = useState(false)
  useEffect(() => {
    if (!busy) return
    const timer = setTimeout(() => setSlow(true), afterMs)
    return () => {
      clearTimeout(timer)
      setSlow(false)
    }
  }, [busy, afterMs])
  return busy && slow
}
