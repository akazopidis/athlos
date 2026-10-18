import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, PlayCircle } from 'lucide-react'
import { Link, useParams } from 'react-router'
import { Loading, Notice } from '@/components/page'
import { ProgramCover } from '@/components/program-cover'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { useApp } from '@/lib/AppContext'
import { categoryLabel } from '@/lib/categories'
import { useTitle } from '@/lib/useTitle'
import { cn } from '@/lib/utils'

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

  useTitle(program.data?.title)

  if (!signedIn) return <Notice title="Sign in to see your programs" />
  if (program.isPending) return <Loading lines={5} />
  if (!program.data) {
    return (
      <Notice
        title="Program not found"
        action={
          <Button asChild variant="outline">
            <Link to="/me">My programs</Link>
          </Button>
        }
      />
    )
  }

  const p = program.data
  const done = p.workouts.filter((w) => w.completedAt).length
  const percent = p.workouts.length ? Math.round((done / p.workouts.length) * 100) : 0
  const weeks = [...new Set(p.workouts.map((w) => Number(w.week)))]
  return (
    <article className="space-y-10">
      <Button asChild variant="ghost" size="sm" className="-ml-3">
        <Link to="/me">
          <ArrowLeft aria-hidden /> My programs
        </Link>
      </Button>
      <header className="grid gap-6 md:grid-cols-[minmax(0,2fr)_minmax(0,3fr)] md:items-center md:gap-10">
        <ProgramCover url={p.coverImageUrl} title={p.title} category={p.category} className="rounded-3xl" />
        <div className="space-y-4">
          <div className="flex flex-wrap gap-2">
            <Badge variant="secondary">{categoryLabel(p.category)}</Badge>
            <Badge variant="outline">{Number(p.durationWeeks)} weeks</Badge>
          </div>
          <h1 className="text-4xl leading-tight font-black tracking-tight sm:text-5xl">{p.title}</h1>
          <p className="text-muted-foreground">
            by{' '}
            <Link to={`/c/${p.coachSlug}`} className="font-medium text-foreground underline-offset-4 hover:underline">
              {p.coachSlug}
            </Link>
          </p>
          <div className="space-y-2 rounded-2xl border bg-card p-4">
            <div className="flex items-baseline justify-between gap-4">
              <p className="font-medium">
                {done} of {p.workouts.length} workouts done
              </p>
              <p className="font-heading text-2xl font-black tabular-nums">{percent}%</p>
            </div>
            <div
              className="h-2.5 w-full overflow-hidden rounded-full bg-muted"
              role="progressbar"
              aria-label="Progress"
              aria-valuenow={percent}
              aria-valuemin={0}
              aria-valuemax={100}
            >
              <div className="h-full rounded-full bg-volt transition-[width] duration-500" style={{ width: `${percent}%` }} />
            </div>
          </div>
        </div>
      </header>

      {p.description && <p className="max-w-prose whitespace-pre-line text-muted-foreground">{p.description}</p>}

      {toggle.error && (
        <Alert variant="destructive">
          <AlertDescription>{toggle.error.message}</AlertDescription>
        </Alert>
      )}
      <div className="space-y-10">
        {weeks.map((week) => {
          const workouts = p.workouts.filter((w) => Number(w.week) === week)
          return (
            <section key={week} className="space-y-3">
              <div className="flex items-baseline justify-between gap-4">
                <h2 className="text-2xl font-bold tracking-tight">Week {week}</h2>
                <p className="text-sm text-muted-foreground tabular-nums">
                  {workouts.filter((w) => w.completedAt).length}/{workouts.length}
                </p>
              </div>
              <ol className="space-y-3">
                {workouts.map((w) => (
                  <li key={w.id} className={cn('rounded-2xl border bg-card p-4 transition-colors sm:p-5', w.completedAt && 'bg-muted/50')}>
                    <div className="flex items-start gap-4">
                      <Checkbox
                        id={`workout-${w.id}`}
                        className="mt-1 size-6 rounded-md"
                        checked={Boolean(w.completedAt)}
                        disabled={toggle.isPending}
                        onCheckedChange={(checked) => toggle.mutate({ workoutId: w.id, done: checked === true })}
                      />
                      <div className="min-w-0 flex-1 space-y-2">
                        <label htmlFor={`workout-${w.id}`} className="block cursor-pointer">
                          <span className="block text-xs font-semibold tracking-wider text-muted-foreground uppercase">Day {Number(w.day)}</span>
                          <span className={cn('block text-lg leading-snug font-semibold', w.completedAt && 'text-muted-foreground line-through')}>{w.title}</span>
                        </label>
                        {w.content && <p className="text-sm whitespace-pre-line text-muted-foreground">{w.content}</p>}
                        {w.videoUrl && (
                          <Button asChild variant="outline" size="sm">
                            <a href={w.videoUrl} target="_blank" rel="noreferrer">
                              <PlayCircle aria-hidden /> Watch the video
                            </a>
                          </Button>
                        )}
                      </div>
                    </div>
                  </li>
                ))}
              </ol>
            </section>
          )
        })}
      </div>
    </article>
  )
}
