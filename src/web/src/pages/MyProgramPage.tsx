import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router'
import { useApp } from '../AppContext.tsx'

// A program the customer bought: its workouts (the paid content) and which are done.
// enrollment-api answers 404 for a program the customer did not buy.
export function MyProgramPage() {
  const { programId = '' } = useParams()
  const { api, msal } = useApp()
  const queryClient = useQueryClient()
  const signedIn = Boolean(msal?.getActiveAccount())

  const program = useQuery({
    queryKey: ['my-program', programId],
    queryFn: async () => (await api.GET('/api/v1/me/programs/{programId}', { params: { path: { programId } } })).data ?? null,
    enabled: signedIn,
  })

  const toggle = useMutation({
    mutationFn: async ({ workoutId, done }: { workoutId: string; done: boolean }) => {
      const params = { params: { path: { programId, workoutId } } }
      const { response } = done
        ? await api.PUT('/api/v1/me/programs/{programId}/workouts/{workoutId}/completion', params)
        : await api.DELETE('/api/v1/me/programs/{programId}/workouts/{workoutId}/completion', params)
      if (!response.ok) throw new Error('Could not save your progress.')
    },
    onSettled: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: ['my-program', programId] }),
      queryClient.invalidateQueries({ queryKey: ['my-programs'] }),
    ]),
  })

  if (!signedIn) return <p>Sign in to see your programs.</p>
  if (program.isPending) return <p>Loading…</p>
  if (!program.data) return <p>Program not found.</p>

  const p = program.data
  const done = p.workouts.filter((w) => w.completedAt).length
  return (
    <article className="program">
      <p>
        <Link to="/me">← My programs</Link>
      </p>
      {p.coverImageUrl && <img src={p.coverImageUrl} alt="" className="cover" />}
      <h1>{p.title}</h1>
      <p>
        By <Link to={`/c/${p.coachSlug}`}>{p.coachSlug}</Link> · {p.level} · {Number(p.durationWeeks)} weeks
      </p>
      <p className="description">{p.description}</p>
      <h2>
        Workouts ({done} of {p.workouts.length} done)
      </h2>
      {toggle.error && <p role="alert">{toggle.error.message}</p>}
      <ol className="workouts">
        {p.workouts.map((w) => (
          <li key={w.id}>
            <label>
              <input
                type="checkbox"
                checked={Boolean(w.completedAt)}
                disabled={toggle.isPending}
                onChange={(e) => toggle.mutate({ workoutId: w.id, done: e.target.checked })}
              />
              <strong>
                Week {Number(w.week)}, day {Number(w.day)}: {w.title}
              </strong>
            </label>
            {w.content && <p className="description">{w.content}</p>}
            {w.videoUrl && (
              <a href={w.videoUrl} target="_blank" rel="noreferrer">
                Watch the video
              </a>
            )}
          </li>
        ))}
      </ol>
    </article>
  )
}
