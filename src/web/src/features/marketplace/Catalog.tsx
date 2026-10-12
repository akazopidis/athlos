import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { ChevronLeft, ChevronRight, SearchX, SlidersHorizontal, X } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import type { components } from '@/api/catalog'
import { selectClassName } from '@/components/field'
import { CardGridSkeleton, Empty } from '@/components/page'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useApp } from '@/lib/AppContext'
import { useCoaches } from '@/lib/useCoaches'
import { useMyWorkspaces } from '@/lib/useMyWorkspaces'
import { useOwnedPrograms } from '@/lib/useOwnedPrograms'
import { cn } from '@/lib/utils'
import { ProductCard } from './ProductCard.tsx'
import { activeFilters, Lengths, Levels, PageSize, Sorts, type ProgramSearch, type SearchChange, type Sort } from './useProgramSearch.ts'

type Results = components['schemas']['SearchProgramsSearchResults']

// The shop's catalog (catalog's SearchPrograms, design §8): filters beside the products on large
// screens and in a drawer on phones, a sort menu and the result count above them, pages below.
// Every coach's programs on the marketplace; one coach's on a storefront (fixedCoach).
export function Catalog({
  search,
  update,
  clear,
  fixedCoach,
}: {
  search: ProgramSearch
  update: (change: SearchChange) => void
  clear: () => void
  fixedCoach?: string
}) {
  const { api } = useApp()
  const owned = useOwnedPrograms()
  const mine = useMyWorkspaces()
  const [filtersOpen, setFiltersOpen] = useState(false)
  const coach = fixedCoach ?? search.coach
  const page = search.page ?? 1
  const length = Lengths.find((l) => l.value === search.length)

  const programs = useQuery({
    queryKey: ['programs', coach ?? null, search.q ?? '', search.level ?? null, search.minPrice ?? null, search.maxPrice ?? null, search.length ?? null, search.sort ?? null, page],
    queryFn: async () =>
      (
        await api.GET('/api/v1/marketplace/programs', {
          params: {
            query: {
              Q: search.q || undefined,
              Level: search.level,
              Coach: coach,
              MinPrice: search.minPrice !== undefined ? search.minPrice * 100 : undefined,
              MaxPrice: search.maxPrice !== undefined ? search.maxPrice * 100 : undefined,
              MinWeeks: length?.min,
              MaxWeeks: length?.max,
              Sort: search.sort,
              Page: page,
              PageSize,
            },
          },
        })
      ).data ?? null,
    // Keep the current results on screen while the next page or search loads.
    placeholderData: keepPreviousData,
  })

  const data = programs.data
  const coaches = useCoaches([...(data?.items.map((p) => p.coachSlug) ?? []), ...(data?.coaches.map((c) => c.coachSlug) ?? [])])
  const total = Number(data?.totalCount ?? 0)
  const pages = Math.max(1, Math.ceil(total / PageSize))
  const filters = activeFilters({ ...search, coach: fixedCoach ? undefined : search.coach })

  const sidebar = (
    <Filters search={search} update={update} clear={clear} results={data} coaches={coaches} hideCoach={Boolean(fixedCoach)} />
  )

  return (
    <div className="grid gap-8 lg:grid-cols-[15rem_minmax(0,1fr)]">
      <aside className="hidden lg:block" aria-label="Filters">
        <div className="sticky top-36">{sidebar}</div>
      </aside>

      <div className="min-w-0 space-y-6">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-sm text-muted-foreground" aria-live="polite">
            {programs.isPending ? 'Loading programs…' : `${total} ${total === 1 ? 'program' : 'programs'}`}
          </p>
          <div className="flex items-center gap-2">
            <Button variant="outline" size="sm" className="lg:hidden" onClick={() => setFiltersOpen(true)} aria-expanded={filtersOpen} aria-controls="filters-drawer">
              <SlidersHorizontal aria-hidden /> Filters{filters > 0 && ` (${filters})`}
            </Button>
            <label className="flex items-center gap-2 text-sm">
              <span className="hidden text-muted-foreground sm:inline">Sort by</span>
              <select
                aria-label="Sort by"
                className={cn(selectClassName, 'h-9 w-auto')}
                value={search.sort ?? 'Newest'}
                onChange={(e) => update({ sort: e.target.value === 'Newest' ? null : (e.target.value as Sort) })}
              >
                {Sorts.map((s) => (
                  <option key={s.value} value={s.value}>
                    {s.label}
                  </option>
                ))}
              </select>
            </label>
          </div>
        </div>

        {programs.isPending ? (
          <CardGridSkeleton />
        ) : !data?.items.length ? (
          <Empty
            icon={<SearchX />}
            title={search.q || filters ? 'No programs match' : 'No programs yet'}
            action={filters > 0 && <Button variant="outline" onClick={clear}>Clear filters</Button>}
          >
            {search.q || filters ? 'Try other words or fewer filters.' : 'New programs appear here as soon as coaches publish them.'}
          </Empty>
        ) : (
          <ul className={cn('grid gap-4 sm:grid-cols-2 sm:gap-5 xl:grid-cols-3', programs.isPlaceholderData && 'opacity-60 transition-opacity')} aria-busy={programs.isFetching}>
            {data.items.map((p) => (
              <li key={p.id}>
                <ProductCard program={p} coach={coaches.get(p.coachSlug)} owned={owned.has(p.id)} mine={mine.has(p.coachSlug)} />
              </li>
            ))}
          </ul>
        )}

        {pages > 1 && (
          <nav className="flex items-center justify-center gap-3" aria-label="Pages">
            <Button variant="outline" disabled={page <= 1} onClick={() => update({ page: page - 1 })}>
              <ChevronLeft aria-hidden /> Previous
            </Button>
            <span className="px-2 text-sm text-muted-foreground tabular-nums">
              Page {page} of {pages}
            </span>
            <Button variant="outline" disabled={page >= pages} onClick={() => update({ page: page + 1 })}>
              Next <ChevronRight aria-hidden />
            </Button>
          </nav>
        )}
      </div>

      {filtersOpen && <FiltersDrawer onClose={() => setFiltersOpen(false)} results={total}>{sidebar}</FiltersDrawer>}
    </div>
  )
}

function Filters({
  search,
  update,
  clear,
  results,
  coaches,
  hideCoach,
}: {
  search: ProgramSearch
  update: (change: SearchChange) => void
  clear: () => void
  results: Results | null | undefined
  coaches: ReturnType<typeof useCoaches>
  hideCoach: boolean
}) {
  const facets = results?.coaches ?? []
  const filters = activeFilters({ ...search, coach: hideCoach ? undefined : search.coach })

  return (
    <div className="space-y-7">
      <div className="flex items-center justify-between">
        <h2 className="font-heading text-lg font-bold">Filters</h2>
        {filters > 0 && (
          <button type="button" onClick={clear} className="text-sm font-medium text-muted-foreground underline-offset-4 hover:text-foreground hover:underline">
            Clear all
          </button>
        )}
      </div>

      <FilterGroup title="Level">
        <Choice name="level" label="All levels" checked={!search.level} onChange={() => update({ level: null })} />
        {Levels.map((level) => (
          <Choice key={level} name="level" label={level} checked={search.level === level} onChange={() => update({ level })} />
        ))}
      </FilterGroup>

      {!hideCoach && facets.length > 0 && (
        <FilterGroup title="Coach">
          <Choice name="coach" label="All coaches" checked={!search.coach} onChange={() => update({ coach: null })} />
          {facets.map((c) => (
            <Choice
              key={c.coachSlug}
              name="coach"
              label={coaches.get(c.coachSlug)?.displayName ?? c.coachSlug}
              count={Number(c.count)}
              checked={search.coach === c.coachSlug}
              onChange={() => update({ coach: c.coachSlug })}
            />
          ))}
        </FilterGroup>
      )}

      <FilterGroup title="Length">
        <Choice name="length" label="Any length" checked={!search.length} onChange={() => update({ length: null })} />
        {Lengths.map((l) => (
          <Choice key={l.value} name="length" label={l.label} checked={search.length === l.value} onChange={() => update({ length: l.value })} />
        ))}
      </FilterGroup>

      <FilterGroup title="Price (€)">
        <PriceRange key={`${search.minPrice}-${search.maxPrice}`} min={search.minPrice} max={search.maxPrice} onApply={(minPrice, maxPrice) => update({ minPrice, maxPrice })} />
      </FilterGroup>
    </div>
  )
}

function FilterGroup({ title, children }: { title: string; children: ReactNode }) {
  return (
    <fieldset className="space-y-1.5">
      <legend className="mb-2 text-xs font-semibold tracking-[0.15em] text-muted-foreground uppercase">{title}</legend>
      {children}
    </fieldset>
  )
}

function Choice({ name, label, count, checked, onChange }: { name: string; label: string; count?: number; checked: boolean; onChange: () => void }) {
  return (
    <label className="flex cursor-pointer items-center gap-2.5 rounded-lg px-2 py-1.5 text-sm hover:bg-muted has-[:focus-visible]:ring-3 has-[:focus-visible]:ring-ring/50">
      <input type="radio" name={name} checked={checked} onChange={onChange} className="size-4 accent-primary" />
      <span className={cn('flex-1 truncate', checked && 'font-semibold')}>{label}</span>
      {count !== undefined && <span className="text-xs text-muted-foreground tabular-nums">{count}</span>}
    </label>
  )
}

function PriceRange({ min, max, onApply }: { min?: number; max?: number; onApply: (min: number | null, max: number | null) => void }) {
  const [from, setFrom] = useState(min?.toString() ?? '')
  const [to, setTo] = useState(max?.toString() ?? '')
  const value = (text: string) => (text.trim() === '' ? null : Math.max(0, Math.floor(Number(text))))

  return (
    <form
      className="flex items-center gap-2"
      onSubmit={(e) => {
        e.preventDefault()
        onApply(value(from), value(to))
      }}
    >
      <Input type="number" inputMode="numeric" min={0} aria-label="Lowest price" placeholder="Min" value={from} onChange={(e) => setFrom(e.target.value)} className="h-9" />
      <span className="text-muted-foreground" aria-hidden>
        –
      </span>
      <Input type="number" inputMode="numeric" min={0} aria-label="Highest price" placeholder="Max" value={to} onChange={(e) => setTo(e.target.value)} className="h-9" />
      <Button type="submit" size="sm" variant="outline" className="h-9">
        Go
      </Button>
    </form>
  )
}

// The filters on phones and tablets: a panel from the left, over the page.
function FiltersDrawer({ onClose, results, children }: { onClose: () => void; results: number; children: ReactNode }) {
  useEffect(() => {
    const close = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    document.addEventListener('keydown', close)
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.removeEventListener('keydown', close)
      document.body.style.overflow = overflow
    }
  }, [onClose])

  return (
    <div className="fixed inset-0 z-50 lg:hidden" role="dialog" aria-modal="true" aria-label="Filters" id="filters-drawer">
      <button type="button" aria-label="Close filters" className="absolute inset-0 bg-black/50 animate-in fade-in-0" onClick={onClose} />
      <div className="absolute inset-y-0 left-0 flex w-[min(22rem,88vw)] flex-col bg-background shadow-xl animate-in slide-in-from-left">
        <div className="flex items-center justify-end border-b p-2">
          <Button variant="ghost" size="icon" aria-label="Close filters" onClick={onClose}>
            <X aria-hidden />
          </Button>
        </div>
        <div className="flex-1 overflow-y-auto p-5">{children}</div>
        <div className="border-t p-4">
          <Button className="w-full" onClick={onClose}>
            Show {results} {results === 1 ? 'program' : 'programs'}
          </Button>
        </div>
      </div>
    </div>
  )
}
