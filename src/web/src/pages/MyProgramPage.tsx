import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, PlayCircle } from 'lucide-react'
import { Link, useParams } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Label } from '@/components/ui/label'
import { Loading } from '@/components/page'
import { ProgramCover } from '@/components/program-cover'
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
  if (program.isPending) return <Loading lines={5} />
  if (!program.data) return <p>Program not found.</p>

  const p = program.data
  const done = p.workouts.filter((w) => w.completedAt).length
  const weeks = [...new Set(p.workouts.map((w) => Number(w.week)))]
  return (
    <article className="space-y-8">
      <Button asChild variant="ghost" size="sm" className="-ml-2">
        <Link to="/me">
          <ArrowLeft aria-hidden /> My programs
        </Link>
      </Button>
      <div className="grid gap-6 md:grid-cols-[2fr_3fr]">
        <ProgramCover url={p.coverImageUrl} className="rounded-xl" />
        <div className="space-y-3">
          <h1 className="text-3xl font-semibold tracking-tight">{p.title}</h1>
          <p className="text-sm text-muted-foreground">
            By{' '}
            <Link to={`/c/${p.coachSlug}`} className="underline-offset-4 hover:underline">
              {p.coachSlug}
            </Link>
          </p>
          <div className="flex gap-2">
            <Badge variant="secondary">{p.level}</Badge>
            <Badge variant="outline">{Number(p.durationWeeks)} weeks</Badge>
          </div>
          <p className="whitespace-pre-line text-muted-foreground">{p.description}</p>
        </div>
      </div>

      <section className="space-y-4">
        <div className="space-y-2">
          <h2 className="text-xl font-semibold tracking-tight">
            Workouts ({done} of {p.workouts.length} done)
          </h2>
          <div className="h-2 w-full overflow-hidden rounded-full bg-muted" aria-hidden>
            <div className="h-full bg-primary transition-all" style={{ width: `${p.workouts.length ? (done / p.workouts.length) * 100 : 0}%` }} />
          </div>
        </div>
        {toggle.error && (
          <Alert variant="destructive">
            <AlertDescription>{toggle.error.message}</AlertDescription>
          </Alert>
        )}
        {weeks.map((week) => (
          <Card key={week}>
            <CardHeader>
              <CardTitle>Week {week}</CardTitle>
            </CardHeader>
            <CardContent>
              <ol className="divide-y">
                {p.workouts
                  .filter((w) => Number(w.week) === week)
                  .map((w) => (
                    <li key={w.id} className="space-y-2 py-4 first:pt-0 last:pb-0">
                      <div className="flex items-center gap-3">
                        <Checkbox
                          id={`workout-${w.id}`}
                          checked={Boolean(w.completedAt)}
                          disabled={toggle.isPending}
                          onCheckedChange={(checked) => toggle.mutate({ workoutId: w.id, done: checked === true })}
                        />
                        <Label htmlFor={`workout-${w.id}`} className={w.completedAt ? 'text-muted-foreground line-through' : undefined}>
                          Week {Number(w.week)}, day {Number(w.day)}: {w.title}
                        </Label>
                      </div>
                      {w.content && <p className="whitespace-pre-line pl-7 text-sm text-muted-foreground">{w.content}</p>}
                      {w.videoUrl && (
                        <a href={w.videoUrl} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 pl-7 text-sm underline-offset-4 hover:underline">
                          <PlayCircle className="size-4" aria-hidden /> Watch the video
                        </a>
                      )}
                    </li>
                  ))}
              </ol>
            </CardContent>
          </Card>
        ))}
      </section>
    </article>
  )
}
