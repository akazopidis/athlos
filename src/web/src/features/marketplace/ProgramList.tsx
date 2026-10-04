import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { ChevronLeft, ChevronRight, SearchX } from 'lucide-react'
import { Link } from 'react-router'
import type { components } from '@/api/catalog'
import { CardGridSkeleton, Empty } from '@/components/page'
import { ProgramCover } from '@/components/program-cover'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { formatMoney } from '@/lib/money'
import { cn } from '@/lib/utils'
import { PageSize, type ProgramSearch } from './useProgramSearch.ts'

// Published programs, newest first (catalog's SearchPrograms, design §8): every coach's on the
// marketplace, one coach's on a storefront. Paged like every list (design §7.1).
export function ProgramList({ search, onPage }: { search: ProgramSearch; onPage: (page: number) => void }) {
  const { api } = useApp()
  const page = search.page ?? 1
  const programs = useQuery({
    queryKey: ['programs', search.coach ?? null, search.q ?? '', search.level ?? null, page],
    queryFn: async () =>
      (
        await api.GET('/api/v1/marketplace/programs', {
          params: { query: { Q: search.q || undefined, Level: search.level, Coach: search.coach, Page: page, PageSize } },
        })
      ).data ?? null,
    // Keep the current results on screen while the next page or search loads.
    placeholderData: keepPreviousData,
  })

  if (programs.isPending) return <CardGridSkeleton />
  if (!programs.data?.items.length) {
    return (
      <Empty icon={<SearchX />} title={search.q || search.level ? 'No programs match your search' : 'No programs yet'}>
        {search.q || search.level ? 'Try other words or another level.' : 'New programs appear here as soon as coaches publish them.'}
      </Empty>
    )
  }

  const total = Number(programs.data.totalCount)
  const pages = Math.max(1, Math.ceil(total / PageSize))
  return (
    <div className="space-y-10">
      <ul className={cn('grid gap-x-5 gap-y-10 sm:grid-cols-2 lg:grid-cols-3', programs.isPlaceholderData && 'opacity-60 transition-opacity')} aria-busy={programs.isFetching}>
        {programs.data.items.map((p) => (
          <li key={`${p.coachSlug}/${p.slug}`}>
            <ProgramCard program={p} showCoach={!search.coach} />
          </li>
        ))}
      </ul>
      {pages > 1 && (
        <nav className="flex items-center justify-center gap-3" aria-label="Pages">
          <Button variant="outline" disabled={page <= 1} onClick={() => onPage(page - 1)}>
            <ChevronLeft aria-hidden /> Previous
          </Button>
          <span className="px-2 text-sm text-muted-foreground tabular-nums">
            Page {page} of {pages}
          </span>
          <Button variant="outline" disabled={page >= pages} onClick={() => onPage(page + 1)}>
            Next <ChevronRight aria-hidden />
          </Button>
        </nav>
      )}
    </div>
  )
}

function ProgramCard({ program: p, showCoach }: { program: components['schemas']['SearchProgramsItem']; showCoach: boolean }) {
  return (
    <Link to={`/c/${p.coachSlug}/programs/${p.slug}`} className="group block rounded-2xl outline-none focus-visible:ring-3 focus-visible:ring-ring/50">
      <div className="relative overflow-hidden rounded-2xl bg-muted">
        <ProgramCover url={p.coverImageUrl} title={p.title} className="transition-transform duration-500 ease-out group-hover:scale-[1.04]" />
        <span className="absolute top-3 left-3 rounded-full bg-background/90 px-2.5 py-1 text-xs font-semibold shadow-sm backdrop-blur">{p.level}</span>
      </div>
      <div className="mt-3 flex items-start justify-between gap-4">
        <div className="min-w-0">
          <h3 className="truncate text-lg leading-snug font-bold underline-offset-4 group-hover:underline">{p.title}</h3>
          <p className="truncate text-sm text-muted-foreground">
            {showCoach && <>by {p.coachSlug} · </>}
            {Number(p.durationWeeks)} weeks
          </p>
        </div>
        <span className="shrink-0 font-heading text-lg font-extrabold tabular-nums">{formatMoney(p.priceAmount, p.currency)}</span>
      </div>
    </Link>
  )
}
