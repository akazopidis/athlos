import { useEffect } from 'react'

// The browser tab's title for the current page: "Page · Athlos", or "Athlos" while it is unknown.
export function useTitle(title?: string | null) {
  useEffect(() => {
    document.title = title ? `${title} · Athlos` : 'Athlos'
  }, [title])
}
