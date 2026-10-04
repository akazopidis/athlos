import { useMutation, useQuery } from '@tanstack/react-query'
import { ChevronRight } from 'lucide-react'
import type { FormEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Field, selectClassName } from '@/components/field'
import { Empty, Loading, Section } from '@/components/page'
import { ProgramCover } from '@/components/program-cover'
import { useApp } from '@/lib/AppContext'
import { formatMoney } from '@/lib/money'
import { problemTitle } from '@/lib/problem'

// The coach's programs (design §8, "/studio/{slug}": programs): the list, each opening its editor,
// and a form to create one.
export function StudioPrograms({ slug }: { slug: string }) {
  const { api } = useApp()
  const navigate = useNavigate()
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
    // Straight to the editor: workouts and a cover come next.
    onSuccess: (created) => navigate(`programs/${created.id}`),
  })

  const onCreate = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    create.mutate(new FormData(e.currentTarget))
  }

  return (
    <Section title="Programs">
      {programs.isPending ? (
        <Loading />
      ) : programs.data?.length ? (
        <ul className="space-y-3">
          {programs.data.map((p) => (
            <li key={p.id}>
              <Link to={`programs/${p.id}`} className="group block">
                <Card className="transition-shadow group-hover:shadow-md">
                  <CardContent className="flex items-center gap-4">
                    <ProgramCover url={p.coverImageUrl} className="w-28 shrink-0 rounded-md" />
                    <div className="min-w-0 flex-1 space-y-1">
                      <p className="truncate font-medium group-hover:underline">{p.title}</p>
                      <p className="text-sm text-muted-foreground">
                        {formatMoney(p.priceAmount, p.currency)} · {p.level} · {Number(p.durationWeeks)} weeks
                      </p>
                    </div>
                    <Badge variant={p.isPublished ? 'default' : 'secondary'}>{p.isPublished ? 'Published' : 'Draft'}</Badge>
                    <ChevronRight className="size-4 text-muted-foreground" aria-hidden />
                  </CardContent>
                </Card>
              </Link>
            </li>
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
              {create.error && (
                <p className="text-sm text-destructive" role="alert">
                  {create.error.message}
                </p>
              )}
            </div>
          </form>
        </CardContent>
      </Card>
    </Section>
  )
}
