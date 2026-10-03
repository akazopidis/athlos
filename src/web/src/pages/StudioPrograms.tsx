import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Field, selectClassName } from '@/components/field'
import { Empty, Loading, Section } from '@/components/page'
import { useApp } from '../AppContext.tsx'
import { formatMoney } from '../money.ts'
import { problemTitle } from '../problem.ts'

// The coach's programs (design §8, "/studio/{slug}": programs). A minimal version: create a program,
// add workouts, publish. Editing and covers come next (phase 6).
export function StudioPrograms({ slug }: { slug: string }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const tenant = { tenantSlug: slug }

  const programs = useQuery({
    queryKey: ['studio-programs', slug],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/programs', { params: { path: tenant } })).data ?? [],
  })

  const create = useMutation({
    mutationFn: async (form: FormData) => {
      const text = (name: string) => String(form.get(name) ?? '').trim()
      const { data, error } = await api.POST('/api/v1/studio/{tenantSlug}/programs', {
        params: { path: tenant },
        body: {
          slug: text('slug'),
          title: text('title'),
          description: text('description'),
          level: text('level') as 'Beginner' | 'Intermediate' | 'Advanced',
          durationWeeks: Number(text('durationWeeks')),
          // The form takes a price in euros; the API takes minor units (CLAUDE.md: money).
          priceAmount: Math.round(Number(text('price')) * 100),
          currency: 'EUR',
        },
      })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not create the program.')
      return data
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['studio-programs', slug] }),
  })

  const onCreate = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    create.mutate(new FormData(form), { onSuccess: () => form.reset() })
  }

  return (
    <Section title="Programs">
      {programs.isPending ? (
        <Loading />
      ) : programs.data?.length ? (
        <ul className="space-y-4">
          {programs.data.map((p) => (
            <StudioProgram key={p.id} slug={slug} program={p} />
          ))}
        </ul>
      ) : (
        <Empty>No programs yet. Create your first one below.</Empty>
      )}

      <Card>
        <CardHeader>
          <CardTitle>New program</CardTitle>
        </CardHeader>
        <CardContent>
          <form onSubmit={onCreate} className="grid gap-4 sm:grid-cols-2">
            <Field id="new-title" label="Title">
              <Input id="new-title" name="title" required maxLength={120} />
            </Field>
            <Field id="new-slug" label="Address" hint="Lowercase letters, digits and dashes; part of the program's link.">
              <Input id="new-slug" name="slug" required pattern="[a-z0-9]+(-[a-z0-9]+)*" placeholder="strong-start" />
            </Field>
            <Field id="new-description" label="Description" className="sm:col-span-2">
              <Textarea id="new-description" name="description" rows={3} />
            </Field>
            <Field id="new-level" label="Level">
              <select id="new-level" name="level" defaultValue="Beginner" className={selectClassName}>
                <option>Beginner</option>
                <option>Intermediate</option>
                <option>Advanced</option>
              </select>
            </Field>
            <div className="grid grid-cols-2 gap-4">
              <Field id="new-weeks" label="Weeks">
                <Input id="new-weeks" name="durationWeeks" type="number" min={1} max={52} defaultValue={4} required />
              </Field>
              <Field id="new-price" label="Price (EUR)">
                <Input id="new-price" name="price" type="number" min={0.5} step={0.01} defaultValue={19} required />
              </Field>
            </div>
            <div className="flex items-center gap-4 sm:col-span-2">
              <Button type="submit" disabled={create.isPending}>
                Create program
              </Button>
              {create.error && <p className="text-sm text-destructive" role="alert">{create.error.message}</p>}
            </div>
          </form>
        </CardContent>
      </Card>
    </Section>
  )
}

interface ProgramSummary {
  id: string
  slug: string
  title: string
  priceAmount: number | string
  currency: string
  isPublished: boolean
}

function StudioProgram({ slug, program }: { slug: string; program: ProgramSummary }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const [adding, setAdding] = useState(false)
  const path = { tenantSlug: slug, programId: program.id }

  const workouts = useQuery({
    queryKey: ['studio-workouts', slug, program.id],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/programs/{programId}/workouts', { params: { path } })).data ?? [],
  })

  const publish = useMutation({
    mutationFn: async () => {
      const { data, error } = program.isPublished
        ? await api.POST('/api/v1/studio/{tenantSlug}/programs/{programId}/unpublish', { params: { path } })
        : await api.POST('/api/v1/studio/{tenantSlug}/programs/{programId}/publish', { params: { path } })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not change the program.')
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['studio-programs', slug] }),
  })

  const addWorkout = useMutation({
    mutationFn: async (form: FormData) => {
      const text = (name: string) => String(form.get(name) ?? '').trim()
      const { data, error } = await api.POST('/api/v1/studio/{tenantSlug}/programs/{programId}/workouts', {
        params: { path },
        body: {
          week: Number(text('week')),
          day: Number(text('day')),
          title: text('title'),
          content: text('content'),
          videoUrl: text('videoUrl') || null,
        },
      })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not add the workout.')
    },
    onSuccess: () => {
      setAdding(false)
      return queryClient.invalidateQueries({ queryKey: ['studio-workouts', slug, program.id] })
    },
  })

  const onAdd = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    addWorkout.mutate(new FormData(e.currentTarget))
  }

  return (
    <li>
      <Card>
        <CardHeader className="flex-row flex-wrap items-center justify-between gap-2">
          <div className="space-y-1">
            <CardTitle>{program.title}</CardTitle>
            <p className="text-sm text-muted-foreground">{formatMoney(program.priceAmount, program.currency)}</p>
          </div>
          <div className="flex items-center gap-2">
            {program.isPublished ? (
              <Button asChild variant="link" size="sm">
                <Link to={`/c/${slug}/programs/${program.slug}`}>View</Link>
              </Button>
            ) : null}
            <Badge variant={program.isPublished ? 'default' : 'secondary'}>{program.isPublished ? 'Published' : 'Draft'}</Badge>
          </div>
        </CardHeader>
        <CardContent className="space-y-4">
          {workouts.data?.length ? (
            <ol className="space-y-1 text-sm">
              {workouts.data.map((w) => (
                <li key={w.id}>
                  <span className="text-muted-foreground">
                    Week {Number(w.week)}, day {Number(w.day)}:
                  </span>{' '}
                  {w.title}
                </li>
              ))}
            </ol>
          ) : (
            <p className="text-sm text-muted-foreground">No workouts yet. Add at least one before publishing.</p>
          )}
          <div className="flex flex-wrap gap-2">
            <Button size="sm" variant={program.isPublished ? 'outline' : 'default'} onClick={() => publish.mutate()} disabled={publish.isPending}>
              {program.isPublished ? 'Unpublish' : 'Publish'}
            </Button>
            <Button size="sm" variant="outline" onClick={() => setAdding(!adding)}>
              Add workout
            </Button>
          </div>
          {publish.error && (
            <Alert variant="destructive">
              <AlertDescription>{publish.error.message}</AlertDescription>
            </Alert>
          )}
          {adding && (
            <form onSubmit={onAdd} className="grid gap-4 rounded-lg border p-4 sm:grid-cols-2">
              <div className="grid grid-cols-2 gap-4">
                <Field id={`week-${program.id}`} label="Week">
                  <Input id={`week-${program.id}`} name="week" type="number" min={1} max={52} defaultValue={1} required />
                </Field>
                <Field id={`day-${program.id}`} label="Day">
                  <Input id={`day-${program.id}`} name="day" type="number" min={1} max={7} defaultValue={(workouts.data?.length ?? 0) + 1} required />
                </Field>
              </div>
              <Field id={`title-${program.id}`} label="Title">
                <Input id={`title-${program.id}`} name="title" required maxLength={120} />
              </Field>
              <Field id={`content-${program.id}`} label="Instructions" className="sm:col-span-2">
                <Textarea id={`content-${program.id}`} name="content" rows={3} />
              </Field>
              <Field id={`video-${program.id}`} label="Video link (optional)" className="sm:col-span-2">
                <Input id={`video-${program.id}`} name="videoUrl" type="url" />
              </Field>
              <div className="flex items-center gap-4 sm:col-span-2">
                <Button type="submit" size="sm" disabled={addWorkout.isPending}>
                  Save workout
                </Button>
                {addWorkout.error && <p className="text-sm text-destructive" role="alert">{addWorkout.error.message}</p>}
              </div>
            </form>
          )}
        </CardContent>
      </Card>
    </li>
  )
}
