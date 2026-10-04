import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, ImagePlus, Pencil, Trash2 } from 'lucide-react'
import { useRef, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Field, selectClassName } from '@/components/field'
import { Loading } from '@/components/page'
import { ProgramCover } from '@/components/program-cover'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'
import { useStudio } from './useStudio.ts'

type Level = 'Beginner' | 'Intermediate' | 'Advanced'

interface Workout {
  id: string
  week: number | string
  day: number | string
  title: string
  content: string
  videoUrl: string | null
}

// One program in the studio: its details, publishing, cover image, workouts, and deleting it while
// it was never on sale (catalog refuses afterwards, design §3.4).
export function StudioProgramEditor() {
  const { slug } = useStudio()
  const { programId = '' } = useParams()
  const { api } = useApp()
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const path = { tenantSlug: slug, programId }

  const program = useQuery({
    queryKey: ['studio-program', slug, programId],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/programs/{programId}', { params: { path } })).data ?? null,
  })
  const workouts = useQuery({
    queryKey: ['studio-workouts', slug, programId],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/programs/{programId}/workouts', { params: { path } })).data ?? [],
  })

  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ['studio-program', slug, programId] }),
      queryClient.invalidateQueries({ queryKey: ['studio-programs', slug] }),
    ])

  const save = useMutation({
    mutationFn: async (form: FormData) => {
      const text = (name: string) => String(form.get(name) ?? '').trim()
      const { data, error } = await api.PUT('/api/v1/studio/{tenantSlug}/programs/{programId}', {
        params: { path },
        body: {
          title: text('title'),
          description: text('description'),
          level: text('level') as Level,
          durationWeeks: Number(text('durationWeeks')),
          priceAmount: Math.round(Number(text('price')) * 100),
          currency: 'EUR',
        },
      })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not save the program.')
    },
    onSuccess: refresh,
  })

  const publish = useMutation({
    mutationFn: async (publishIt: boolean) => {
      const { data, error } = publishIt
        ? await api.POST('/api/v1/studio/{tenantSlug}/programs/{programId}/publish', { params: { path } })
        : await api.POST('/api/v1/studio/{tenantSlug}/programs/{programId}/unpublish', { params: { path } })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not change the program.')
    },
    onSuccess: refresh,
  })

  const remove = useMutation({
    mutationFn: async () => {
      const { error, response } = await api.DELETE('/api/v1/studio/{tenantSlug}/programs/{programId}', { params: { path } })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not delete the program.')
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['studio-programs', slug] })
      navigate(`/studio/${slug}`)
    },
  })

  if (program.isPending) return <Loading lines={6} />
  if (!program.data) return <p>Program not found.</p>

  const p = program.data
  return (
    <div className="space-y-8">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <Button asChild variant="ghost" size="sm" className="-ml-2">
          <Link to={`/studio/${slug}`}>
            <ArrowLeft aria-hidden /> All programs
          </Link>
        </Button>
        <div className="flex items-center gap-2">
          <Badge variant={p.isPublished ? 'default' : 'secondary'}>{p.isPublished ? 'Published' : 'Draft'}</Badge>
          {p.isPublished && (
            <Button asChild variant="outline" size="sm">
              <Link to={`/c/${slug}/programs/${p.slug}`}>View</Link>
            </Button>
          )}
          <Button size="sm" variant={p.isPublished ? 'outline' : 'default'} onClick={() => publish.mutate(!p.isPublished)} disabled={publish.isPending}>
            {p.isPublished ? 'Unpublish' : 'Publish'}
          </Button>
        </div>
      </div>
      {publish.error && (
        <Alert variant="destructive">
          <AlertDescription>{publish.error.message}</AlertDescription>
        </Alert>
      )}

      <div className="grid gap-6 lg:grid-cols-[3fr_2fr]">
        <Card>
          <CardHeader>
            <CardTitle>Details</CardTitle>
          </CardHeader>
          <CardContent>
            <form
              key={p.updatedAt}
              onSubmit={(e: FormEvent<HTMLFormElement>) => {
                e.preventDefault()
                save.mutate(new FormData(e.currentTarget))
              }}
              className="grid gap-4 sm:grid-cols-2"
            >
              <Field id="title" label="Title" className="sm:col-span-2">
                <Input id="title" name="title" required maxLength={120} defaultValue={p.title} />
              </Field>
              <Field id="description" label="Description" className="sm:col-span-2">
                <Textarea id="description" name="description" rows={4} defaultValue={p.description} />
              </Field>
              <Field id="level" label="Level">
                <select id="level" name="level" defaultValue={p.level} className={selectClassName}>
                  <option>Beginner</option>
                  <option>Intermediate</option>
                  <option>Advanced</option>
                </select>
              </Field>
              <div className="grid grid-cols-2 gap-4">
                <Field id="weeks" label="Weeks">
                  <Input id="weeks" name="durationWeeks" type="number" min={1} max={52} required defaultValue={Number(p.durationWeeks)} />
                </Field>
                <Field id="price" label="Price (EUR)">
                  <Input id="price" name="price" type="number" min={0.5} step={0.01} required defaultValue={Number(p.priceAmount) / 100} />
                </Field>
              </div>
              <div className="flex items-center gap-4 sm:col-span-2">
                <Button type="submit" disabled={save.isPending}>
                  Save
                </Button>
                {save.isSuccess && !save.isPending && <span className="text-sm text-muted-foreground">Saved.</span>}
                {save.error && <span className="text-sm text-destructive">{save.error.message}</span>}
              </div>
            </form>
          </CardContent>
        </Card>
        <CoverCard slug={slug} programId={programId} coverImageUrl={p.coverImageUrl} onChanged={refresh} />
      </div>

      <Workouts slug={slug} programId={programId} workouts={workouts.data ?? []} />

      <Card className="border-destructive/40">
        <CardHeader>
          <CardTitle>Delete program</CardTitle>
          <CardDescription>
            {p.everPublished
              ? 'This program has been on sale, so customers may own it. Unpublish it to take it off the marketplace; it cannot be deleted.'
              : 'The program and its workouts are removed for good.'}
          </CardDescription>
        </CardHeader>
        {!p.everPublished && (
          <CardContent className="flex items-center gap-4">
            <DeleteButton onConfirm={() => remove.mutate()} disabled={remove.isPending}>
              Delete program
            </DeleteButton>
            {remove.error && <span className="text-sm text-destructive">{remove.error.message}</span>}
          </CardContent>
        )}
      </Card>
    </div>
  )
}

// A two-step delete inside the page (no browser confirm dialog).
function DeleteButton({ onConfirm, disabled, children }: { onConfirm: () => void; disabled?: boolean; children: string }) {
  const [armed, setArmed] = useState(false)
  return armed ? (
    <span className="flex items-center gap-2">
      <Button variant="destructive" size="sm" onClick={onConfirm} disabled={disabled}>
        Yes, {children.toLowerCase()}
      </Button>
      <Button variant="ghost" size="sm" onClick={() => setArmed(false)}>
        Cancel
      </Button>
    </span>
  ) : (
    <Button variant="outline" size="sm" onClick={() => setArmed(true)}>
      <Trash2 aria-hidden /> {children}
    </Button>
  )
}

// The cover: JPEG, PNG or WebP up to 2 MB (catalog checks the file's first bytes, design §2.3).
function CoverCard({ slug, programId, coverImageUrl, onChanged }: { slug: string; programId: string; coverImageUrl?: string | null; onChanged: () => void }) {
  const { api } = useApp()
  const input = useRef<HTMLInputElement>(null)
  const path = { tenantSlug: slug, programId }

  const upload = useMutation({
    mutationFn: async (file: File) => {
      if (file.size > 2 * 1024 * 1024) throw new Error('The image must be 2 MB or smaller.')
      const { data, error } = await api.PUT('/api/v1/studio/{tenantSlug}/programs/{programId}/cover', {
        params: { path },
        // The raw image is the body; catalog reads its type from the first bytes.
        body: file as never,
        bodySerializer: (body: unknown) => body as BodyInit,
        headers: { 'Content-Type': file.type || 'application/octet-stream' },
      })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not upload the image. Use a JPEG, PNG or WebP file.')
    },
    onSuccess: onChanged,
  })

  const clear = useMutation({
    mutationFn: async () => {
      const { response } = await api.DELETE('/api/v1/studio/{tenantSlug}/programs/{programId}/cover', { params: { path } })
      if (!response.ok) throw new Error('Could not remove the image.')
    },
    onSuccess: onChanged,
  })

  return (
    <Card>
      <CardHeader>
        <CardTitle>Cover image</CardTitle>
        <CardDescription>JPEG, PNG or WebP, up to 2 MB.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <ProgramCover url={coverImageUrl} className="rounded-xl" />
        <input
          ref={input}
          type="file"
          accept="image/jpeg,image/png,image/webp"
          className="hidden"
          aria-label="Cover image file"
          onChange={(e) => {
            const file = e.target.files?.[0]
            if (file) upload.mutate(file)
            e.target.value = ''
          }}
        />
        <div className="flex gap-2">
          <Button size="sm" variant="outline" onClick={() => input.current?.click()} disabled={upload.isPending}>
            <ImagePlus aria-hidden /> {coverImageUrl ? 'Replace' : 'Upload'}
          </Button>
          {coverImageUrl && (
            <Button size="sm" variant="ghost" onClick={() => clear.mutate()} disabled={clear.isPending}>
              Remove
            </Button>
          )}
        </div>
        {(upload.error ?? clear.error) && <p className="text-sm text-destructive">{(upload.error ?? clear.error)?.message}</p>}
      </CardContent>
    </Card>
  )
}

function Workouts({ slug, programId, workouts }: { slug: string; programId: string; workouts: Workout[] }) {
  const [adding, setAdding] = useState(false)
  const nextDay = workouts.length ? Math.min(7, Number(workouts[workouts.length - 1].day) + 1) : 1
  const nextWeek = workouts.length ? Number(workouts[workouts.length - 1].week) : 1

  return (
    <Card>
      <CardHeader>
        <CardTitle>Workouts</CardTitle>
        <CardDescription>The paid content: buyers see it, nobody else does.</CardDescription>
        {!adding && (
          <CardAction>
            <Button size="sm" onClick={() => setAdding(true)}>
              Add workout
            </Button>
          </CardAction>
        )}
      </CardHeader>
      <CardContent className="space-y-3">
        {workouts.length === 0 && !adding && <p className="text-sm text-muted-foreground">No workouts yet. Add at least one before publishing.</p>}
        <ol className="divide-y rounded-lg border empty:hidden">
          {workouts.map((w) => (
            <WorkoutRow key={w.id} slug={slug} programId={programId} workout={w} />
          ))}
        </ol>
        {adding && (
          <WorkoutForm
            slug={slug}
            programId={programId}
            initial={{ week: nextWeek, day: nextDay, title: '', content: '', videoUrl: null }}
            onDone={() => setAdding(false)}
          />
        )}
      </CardContent>
    </Card>
  )
}

function WorkoutRow({ slug, programId, workout }: { slug: string; programId: string; workout: Workout }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState(false)

  const remove = useMutation({
    mutationFn: async () => {
      const { response } = await api.DELETE('/api/v1/studio/{tenantSlug}/programs/{programId}/workouts/{workoutId}', {
        params: { path: { tenantSlug: slug, programId, workoutId: workout.id } },
      })
      if (!response.ok) throw new Error('Could not delete the workout.')
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['studio-workouts', slug, programId] }),
  })

  if (editing) {
    return (
      <li className="p-3">
        <WorkoutForm slug={slug} programId={programId} initial={workout} onDone={() => setEditing(false)} />
      </li>
    )
  }

  return (
    <li className="flex items-start justify-between gap-4 p-3">
      <div className="min-w-0 space-y-1">
        <p className="text-sm">
          <span className="text-muted-foreground">
            Week {Number(workout.week)}, day {Number(workout.day)}:
          </span>{' '}
          <span className="font-medium">{workout.title}</span>
        </p>
        {workout.content && <p className="line-clamp-2 text-sm text-muted-foreground">{workout.content}</p>}
        {remove.error && <p className="text-sm text-destructive">{remove.error.message}</p>}
      </div>
      <div className="flex shrink-0 gap-1">
        <Button size="icon-sm" variant="ghost" aria-label={`Edit ${workout.title}`} onClick={() => setEditing(true)}>
          <Pencil aria-hidden />
        </Button>
        <Button size="icon-sm" variant="ghost" aria-label={`Delete ${workout.title}`} onClick={() => remove.mutate()} disabled={remove.isPending}>
          <Trash2 aria-hidden />
        </Button>
      </div>
    </li>
  )
}

function WorkoutForm({
  slug,
  programId,
  initial,
  onDone,
}: {
  slug: string
  programId: string
  initial: Omit<Workout, 'id'> & { id?: string }
  onDone: () => void
}) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const prefix = initial.id ?? 'new'

  const save = useMutation({
    mutationFn: async (form: FormData) => {
      const text = (name: string) => String(form.get(name) ?? '').trim()
      const body = {
        week: Number(text('week')),
        day: Number(text('day')),
        title: text('title'),
        content: text('content'),
        videoUrl: text('videoUrl') || null,
      }
      const { data, error } = initial.id
        ? await api.PUT('/api/v1/studio/{tenantSlug}/programs/{programId}/workouts/{workoutId}', {
            params: { path: { tenantSlug: slug, programId, workoutId: initial.id } },
            body,
          })
        : await api.POST('/api/v1/studio/{tenantSlug}/programs/{programId}/workouts', { params: { path: { tenantSlug: slug, programId } }, body })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not save the workout.')
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['studio-workouts', slug, programId] })
      onDone()
    },
  })

  return (
    <form
      onSubmit={(e: FormEvent<HTMLFormElement>) => {
        e.preventDefault()
        save.mutate(new FormData(e.currentTarget))
      }}
      className="grid gap-4 rounded-lg border p-4 sm:grid-cols-2"
    >
      <div className="grid grid-cols-2 gap-4">
        <Field id={`${prefix}-week`} label="Week">
          <Input id={`${prefix}-week`} name="week" type="number" min={1} max={52} required defaultValue={Number(initial.week)} />
        </Field>
        <Field id={`${prefix}-day`} label="Day">
          <Input id={`${prefix}-day`} name="day" type="number" min={1} max={7} required defaultValue={Number(initial.day)} />
        </Field>
      </div>
      <Field id={`${prefix}-title`} label="Title">
        <Input id={`${prefix}-title`} name="title" required maxLength={120} defaultValue={initial.title} />
      </Field>
      <Field id={`${prefix}-content`} label="Instructions" className="sm:col-span-2">
        <Textarea id={`${prefix}-content`} name="content" rows={4} defaultValue={initial.content} />
      </Field>
      <Field id={`${prefix}-video`} label="Video link (optional)" hint="An unlisted YouTube or Vimeo link; videos are not uploaded to Athlos." className="sm:col-span-2">
        <Input id={`${prefix}-video`} name="videoUrl" type="url" defaultValue={initial.videoUrl ?? ''} />
      </Field>
      <div className="flex items-center gap-2 sm:col-span-2">
        <Button type="submit" size="sm" disabled={save.isPending}>
          Save workout
        </Button>
        <Button type="button" size="sm" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
        {save.error && <span className="text-sm text-destructive">{save.error.message}</span>}
      </div>
    </form>
  )
}
