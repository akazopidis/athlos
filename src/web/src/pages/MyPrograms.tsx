import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
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
    <section>
      <h1>My programs</h1>
      {waiting && <p role="status">Setting up your access to {returned.programTitle}…</p>}
      {programs.isPending ? (
        <p>Loading…</p>
      ) : programs.data?.items.length ? (
        <ul>
          {programs.data.items.map((p) => (
            <li key={p.programId}>
              <Link to={`/me/programs/${p.programId}`}>{p.programTitle}</Link> by <Link to={`/c/${p.coachSlug}`}>{p.coachSlug}</Link> ·{' '}
              {Number(p.completedWorkouts)} {Number(p.completedWorkouts) === 1 ? 'workout' : 'workouts'} done
            </li>
          ))}
        </ul>
      ) : (
        <p>
          No programs yet. <Link to="/">Browse programs</Link>
        </p>
      )}
    </section>
  )
}
