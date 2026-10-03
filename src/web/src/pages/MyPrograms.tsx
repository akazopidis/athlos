import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Card, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Empty, Loading, Section } from '@/components/page'
import { useApp } from '../AppContext.tsx'
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
      returned?.status === 'Paid' && !query.state.data?.items.some((p) => p.programId === returned.programId) ? 3000 : false,
  })

  const waiting = returned?.status === 'Paid' && programs.data && !programs.data.items.some((p) => p.programId === returned.programId)

  return (
    <Section title="My programs">
      {waiting && (
        <Alert>
          <AlertDescription>Setting up your access to {returned.programTitle}…</AlertDescription>
        </Alert>
      )}
      {programs.isPending ? (
        <Loading />
      ) : programs.data?.items.length ? (
        <ul className="grid gap-4 sm:grid-cols-2">
          {programs.data.items.map((p) => (
            <li key={p.programId}>
              <Link to={`/me/programs/${p.programId}`} className="group block">
                <Card className="transition-shadow group-hover:shadow-md">
                  <CardHeader>
                    <CardTitle className="group-hover:underline">{p.programTitle}</CardTitle>
                    <CardDescription>
                      by {p.coachSlug} · {Number(p.completedWorkouts)} {Number(p.completedWorkouts) === 1 ? 'workout' : 'workouts'} done
                    </CardDescription>
                  </CardHeader>
                </Card>
              </Link>
            </li>
          ))}
        </ul>
      ) : (
        <Empty>
          No programs yet.{' '}
          <Link to="/" className="underline">
            Browse programs
          </Link>
        </Empty>
      )}
    </Section>
  )
}
