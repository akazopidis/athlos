import { useQuery } from '@tanstack/react-query'
import { Dumbbell, Loader2 } from 'lucide-react'
import { Link } from 'react-router'
import { CardGridSkeleton, Empty, Section } from '@/components/page'
import { ProgramCover } from '@/components/program-cover'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { useMyOrders } from './useMyOrders.ts'

// The programs the customer bought (design §8, "/me": purchased programs and progress).
// Access follows payment: enrollment-api creates it from OrderPaid, a few seconds after the order is
// Paid (longer if enrollment-api is starting from zero). Until then the list is re-read.
export function MyPrograms() {
  const { api } = useApp()
  const { returned } = useMyOrders()

  const programs = useQuery({
    queryKey: ['my-programs'],
    queryFn: async () => (await api.GET('/api/v1/me/programs')).data ?? null,
    refetchInterval: (query) =>
      returned.some((o) => o.status === 'Paid' && !query.state.data?.items.some((p) => p.programId === o.programId)) ? 3000 : false,
  })

  const waiting = programs.data ? returned.filter((o) => o.status === 'Paid' && !programs.data?.items.some((p) => p.programId === o.programId)) : []

  return (
    <Section title="My programs">
      {waiting.length > 0 && (
        <Alert>
          <Loader2 className="animate-spin" aria-hidden />
          <AlertDescription>Setting up your access to {waiting.map((o) => o.programTitle).join(', ')}…</AlertDescription>
        </Alert>
      )}
      {programs.isPending ? (
        <CardGridSkeleton count={3} />
      ) : programs.data?.items.length ? (
        <ul className="grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
          {programs.data.items.map((p) => {
            const done = Number(p.completedWorkouts)
            return (
              <li key={p.programId}>
                <Link
                  to={`/me/programs/${p.programId}`}
                  className="group flex h-full flex-col overflow-hidden rounded-2xl border bg-card transition-shadow outline-none hover:shadow-lg focus-visible:ring-3 focus-visible:ring-ring/50"
                >
                  <ProgramCover title={p.programTitle} className="aspect-[2/1]" />
                  <div className="flex flex-1 flex-col gap-1 p-4">
                    <h3 className="text-lg leading-snug font-bold underline-offset-4 group-hover:underline">{p.programTitle}</h3>
                    <p className="text-sm text-muted-foreground">by {p.coachSlug}</p>
                    <p className="mt-auto pt-3 text-sm font-medium">
                      {done === 0 ? 'Not started yet' : `${done} ${done === 1 ? 'workout' : 'workouts'} done`}
                    </p>
                  </div>
                </Link>
              </li>
            )
          })}
        </ul>
      ) : (
        <Empty
          icon={<Dumbbell />}
          title="No programs yet"
          action={
            <Button asChild>
              <Link to="/">Browse programs</Link>
            </Button>
          }
        >
          Programs you buy appear here, with your progress.
        </Empty>
      )}
    </Section>
  )
}
