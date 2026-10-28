import { useSearchParams } from 'react-router'
import { isCategory, type Category } from '@/lib/categories'

export type Sort = 'Relevance' | 'Newest' | 'TopRated' | 'PriceLowToHigh' | 'PriceHighToLow'

// Best match is offered with a search only, and is then the default (design §8); Newest otherwise.
export const Sorts: { value: Sort; label: string }[] = [
  { value: 'Relevance', label: 'Best match' },
  { value: 'Newest', label: 'Newest' },
  { value: 'TopRated', label: 'Top rated' },
  { value: 'PriceLowToHigh', label: 'Price: low to high' },
  { value: 'PriceHighToLow', label: 'Price: high to low' },
]

// Program length, as shop-style ranges of weeks.
export const Lengths = [
  { value: 'short', label: 'Up to 4 weeks', min: undefined, max: 4 },
  { value: 'medium', label: '5 to 8 weeks', min: 5, max: 8 },
  { value: 'long', label: '9 to 12 weeks', min: 9, max: 12 },
  { value: 'extended', label: 'More than 12 weeks', min: 13, max: undefined },
] as const

export type Length = (typeof Lengths)[number]['value']

// What the API sorts by when the search names no sort.
export const defaultSort = (q: string | undefined): Sort => (q?.trim() ? 'Relevance' : 'Newest')

export interface ProgramSearch {
  q?: string
  category?: Category
  coach?: string
  // Whole currency units in the URL and the form; the API takes minor units.
  minPrice?: number
  maxPrice?: number
  length?: Length
  sort?: Sort
  page?: number
}

export type SearchChange = { [K in keyof ProgramSearch]?: ProgramSearch[K] | null }

export const PageSize = 12

const wholeNumber = (value: string | null) => {
  const n = Number(value)
  return value !== null && value !== '' && Number.isInteger(n) && n >= 0 ? n : undefined
}

// The search lives in the URL (?q=&category=&coach=&minPrice=&maxPrice=&length=&sort=&page=), so a search
// can be shared and the back button works.
export function useProgramSearch() {
  const [params, setParams] = useSearchParams()

  const category = params.get('category')
  const length = params.get('length')
  const sort = params.get('sort')
  const page = Number(params.get('page'))
  const search: ProgramSearch = {
    q: params.get('q') ?? undefined,
    category: isCategory(category) ? category : undefined,
    coach: params.get('coach') ?? undefined,
    minPrice: wholeNumber(params.get('minPrice')),
    maxPrice: wholeNumber(params.get('maxPrice')),
    length: Lengths.some((l) => l.value === length) ? (length as Length) : undefined,
    sort: Sorts.some((s) => s.value === sort) ? (sort as Sort) : undefined,
    page: Number.isInteger(page) && page > 1 ? page : 1,
  }

  // Any change other than the page starts again at page 1. Typing replaces the history entry.
  const update = (change: SearchChange) =>
    setParams(
      (current) => {
        const next = new URLSearchParams(current)
        for (const [key, value] of Object.entries(change)) {
          if (key === 'page') continue
          if (value === null || value === undefined || String(value).trim() === '') next.delete(key)
          else next.set(key, String(value))
        }
        if (change.page && change.page > 1) next.set('page', String(change.page))
        else next.delete('page')
        return next
      },
      { replace: change.q !== undefined },
    )

  const clear = () => setParams((current) => (current.get('q') ? new URLSearchParams({ q: current.get('q')! }) : new URLSearchParams()))

  return { search, update, clear }
}

// How many filters are set (not the text search or the sort), for the phone's "Filters (2)" button.
export function activeFilters(search: ProgramSearch) {
  return [search.category, search.coach, search.minPrice, search.maxPrice, search.length].filter((v) => v !== undefined).length
}
