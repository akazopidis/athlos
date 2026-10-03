import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Empty, Loading } from '@/components/page'
import { ProgramCover } from '@/components/program-cover'
import { useApp } from '../AppContext.tsx'
import { formatMoney } from '../money.ts'

// Published programs, newest first: every coach's on the marketplace, one coach's on a storefront.
export function ProgramList({ coach }: { coach?: string }) {
  const { api } = useApp()
  const programs = useQuery({
    queryKey: ['programs', coach ?? null],
    queryFn: async () => (await api.GET('/api/v1/marketplace/programs', { params: { query: { Coach: coach } } })).data ?? null,
  })

  if (programs.isPending) return <Loading />
  if (!programs.data?.items.length) return <Empty>No programs yet.</Empty>

  return (
    <ul className="grid gap-6 sm:grid-cols-2 lg:grid-cols-3">
      {programs.data.items.map((p) => (
        <li key={`${p.coachSlug}/${p.slug}`}>
          <Link to={`/c/${p.coachSlug}/programs/${p.slug}`} className="group block h-full">
            <Card className="h-full transition-shadow group-hover:shadow-md">
              <ProgramCover url={p.coverImageUrl} />
              <CardHeader>
                <CardTitle className="group-hover:underline">{p.title}</CardTitle>
                <CardDescription>by {p.coachSlug}</CardDescription>
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
  )
}
