import { useSearchParams } from 'react-router'

export type Level = 'Beginner' | 'Intermediate' | 'Advanced'

export const Levels: Level[] = ['Beginner', 'Intermediate', 'Advanced']

export interface ProgramSearch {
  q?: string
  level?: Level
  coach?: string
  page?: number
}

export const PageSize = 12

// The search lives in the URL (?q=&level=&page=), so a search can be shared and the back button works.
export function useProgramSearch() {
  const [params, setParams] = useSearchParams()

  const level = params.get('level')
  const page = Number(params.get('page'))
  const search: ProgramSearch = {
    q: params.get('q') ?? undefined,
    level: Levels.includes(level as Level) ? (level as Level) : undefined,
    page: Number.isInteger(page) && page > 1 ? page : 1,
  }

  // Any change other than the page starts again at page 1. Typing replaces the history entry.
  const update = (change: { q?: string; level?: Level | null; page?: number }) =>
    setParams(
      (current) => {
        const next = new URLSearchParams(current)
        if (change.q !== undefined) {
          if (change.q.trim()) next.set('q', change.q)
          else next.delete('q')
        }
        if (change.level !== undefined) {
          if (change.level) next.set('level', change.level)
          else next.delete('level')
        }
        if (change.page && change.page > 1) next.set('page', String(change.page))
        else next.delete('page')
        return next
      },
      { replace: change.q !== undefined },
    )

  return { search, update }
}
