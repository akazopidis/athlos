import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { useApp } from '../AppContext.tsx'
import { formatMoney } from '../money.ts'
import { problemTitle } from '../problem.ts'

// The coach's programs (design §8, "/studio/{slug}": programs). A minimal version: create a program,
// add workouts, publish. Editing and covers come with the full studio in phase 6.
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
    <section>
      <h2>Programs</h2>
      {programs.isPending ? (
        <p>Loading…</p>
      ) : programs.data?.length ? (
        <ul className="studio-programs">
          {programs.data.map((p) => (
            <StudioProgram key={p.id} slug={slug} program={p} />
          ))}
        </ul>
      ) : (
        <p>No programs yet.</p>
      )}

      <h3>New program</h3>
      <form onSubmit={onCreate}>
        <label>
          Title
          <input name="title" required maxLength={120} />
        </label>
        <label>
          Address
          <input name="slug" required pattern="[a-z0-9]+(-[a-z0-9]+)*" placeholder="strong-start" />
          <span className="hint">Lowercase letters, digits and dashes; part of the program's link.</span>
        </label>
        <label>
          Description
          <textarea name="description" rows={3} />
        </label>
        <label>
          Level
          <select name="level" defaultValue="Beginner">
            <option>Beginner</option>
            <option>Intermediate</option>
            <option>Advanced</option>
          </select>
        </label>
        <label>
          Weeks
          <input name="durationWeeks" type="number" min={1} max={52} defaultValue={4} required />
        </label>
        <label>
          Price (EUR)
          <input name="price" type="number" min={0.5} step={0.01} defaultValue={19} required />
        </label>
        <button type="submit" disabled={create.isPending}>
          Create program
        </button>
        {create.error && <p role="alert">{create.error.message}</p>}
      </form>
    </section>
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
      <strong>{program.title}</strong> · {formatMoney(program.priceAmount, program.currency)} ·{' '}
      {program.isPublished ? (
        <>
          published (<Link to={`/c/${slug}/programs/${program.slug}`}>view</Link>)
        </>
      ) : (
        'draft'
      )}
      <ol>
        {workouts.data?.map((w) => (
          <li key={w.id}>
            Week {Number(w.week)}, day {Number(w.day)}: {w.title}
          </li>
        ))}
      </ol>
      <p>
        <button type="button" onClick={() => publish.mutate()} disabled={publish.isPending}>
          {program.isPublished ? 'Unpublish' : 'Publish'}
        </button>{' '}
        <button type="button" onClick={() => setAdding(!adding)}>
          Add workout
        </button>
      </p>
      {publish.error && <p role="alert">{publish.error.message}</p>}
      {adding && (
        <form onSubmit={onAdd}>
          <label>
            Week
            <input name="week" type="number" min={1} max={52} defaultValue={1} required />
          </label>
          <label>
            Day
            <input name="day" type="number" min={1} max={7} defaultValue={(workouts.data?.length ?? 0) + 1} required />
          </label>
          <label>
            Title
            <input name="title" required maxLength={120} />
          </label>
          <label>
            Instructions
            <textarea name="content" rows={3} />
          </label>
          <label>
            Video link (optional)
            <input name="videoUrl" type="url" />
          </label>
          <button type="submit" disabled={addWorkout.isPending}>
            Save workout
          </button>
          {addWorkout.error && <p role="alert">{addWorkout.error.message}</p>}
        </form>
      )}
    </li>
  )
}
