import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { ChevronLeft, ChevronRight } from 'lucide-react'
import { Link } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Empty, Loading } from '@/components/page'
import { ProgramCover } from '@/components/program-cover'
import { useApp } from '@/lib/AppContext'
import { formatMoney } from '@/lib/money'
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

  if (programs.isPending) return <Loading />
  if (!programs.data?.items.length) {
    return <Empty>{search.q || search.level ? 'No programs match your search.' : 'No programs yet.'}</Empty>
  }

  const total = Number(programs.data.totalCount)
  const pages = Math.max(1, Math.ceil(total / PageSize))
  return (
    <div className="space-y-6">
      <ul className="grid gap-6 sm:grid-cols-2 lg:grid-cols-3" aria-busy={programs.isFetching}>
        {programs.data.items.map((p) => (
          <li key={`${p.coachSlug}/${p.slug}`}>
            <Link to={`/c/${p.coachSlug}/programs/${p.slug}`} className="group block h-full">
              <Card className="h-full transition-shadow group-hover:shadow-md">
                <ProgramCover url={p.coverImageUrl} />
                <CardHeader>
                  <CardTitle className="group-hover:underline">{p.title}</CardTitle>
                  {!search.coach && <CardDescription>by {p.coachSlug}</CardDescription>}
                </CardHeader>
                <CardContent className="flex items-center justify-between gap-2">
                  <div className="flex gap-2">
                    <Badge variant="secondary">{p.level}</Badge>
                    <Badge variant="outline">{Number(p.durationWeeks)} weeks</Badge>
                  </div>
                  <span className="font-semibold">{formatMoney(p.priceAmount, p.currency)}</span>
                </CardContent>
              </Card>
            </Link>
          </li>
        ))}
      </ul>
      {pages > 1 && (
        <nav className="flex items-center justify-center gap-4" aria-label="Pages">
          <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>
            <ChevronLeft aria-hidden /> Previous
          </Button>
          <span className="text-sm text-muted-foreground">
            Page {page} of {pages}
          </span>
          <Button variant="outline" size="sm" disabled={page >= pages} onClick={() => onPage(page + 1)}>
            Next <ChevronRight aria-hidden />
          </Button>
        </nav>
      )}
    </div>
  )
}
