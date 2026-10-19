import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Tag, Trash2 } from 'lucide-react'
import { useState } from 'react'
import type { components } from '@/api/ordering'
import { Field, selectClassName } from '@/components/field'
import { Empty, Loading } from '@/components/page'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'
import { useStudio } from './useStudio.ts'

type Code = components['schemas']['StudioDiscountsItem']

// The workspace's discount codes (design §2.4, ordering-api): a percentage off one program or all of
// them, optionally until a date and for a number of uses. Customers enter the code in their cart; it
// takes nothing off other coaches' programs. The fee is taken from what the customer pays.
export function StudioDiscounts() {
  const { slug } = useStudio()
  const { api } = useApp()
  const tenant = { tenantSlug: slug }

  const codes = useQuery({
    queryKey: ['discounts', slug],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/discounts', { params: { path: tenant } })).data ?? null,
  })
  const programs = useQuery({
    queryKey: ['studio-programs', slug],
    queryFn: async () => (await api.GET('/api/v1/studio/{tenantSlug}/programs', { params: { path: tenant } })).data ?? [],
  })
  const titles = new Map((programs.data ?? []).map((p) => [p.id, p.title]))

  if (codes.isPending) return <Loading />

  return (
    <div className="grid gap-8 lg:grid-cols-[minmax(0,1fr)_22rem] lg:items-start">
      <section aria-labelledby="codes-heading" className="space-y-4">
        <h2 id="codes-heading" className="text-xl font-bold">
          Discount codes
        </h2>
        {codes.data?.length ? (
          <ul className="space-y-3">
            {codes.data.map((c) => (
              <CodeRow key={`${c.id}-${c.isActive}`} code={c} slug={slug} programTitle={c.programId ? (titles.get(c.programId) ?? 'One program') : 'All programs'} />
            ))}
          </ul>
        ) : (
          <Empty icon={<Tag />} title="No codes yet">
            Create a code to share with your followers. It takes a percentage off your programs in their cart.
          </Empty>
        )}
      </section>
      <NewCode slug={slug} programs={(programs.data ?? []).map((p) => ({ id: p.id, title: p.title }))} />
    </div>
  )
}

function CodeRow({ code: c, slug, programTitle }: { code: Code; slug: string; programTitle: string }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const path = { params: { path: { tenantSlug: slug, discountId: c.id } } }
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['discounts', slug] })
  // When the page opened: enough to label a code that has ended.
  const [now] = useState(() => Date.now())
  const expired = c.expiresAt ? new Date(c.expiresAt).getTime() <= now : false
  const uses = Number(c.redemptions)
  const max = c.maxRedemptions == null ? null : Number(c.maxRedemptions)

  const toggle = useMutation({
    mutationFn: async () => {
      const { error, response } = await api.PUT('/api/v1/studio/{tenantSlug}/discounts/{discountId}', {
        ...path,
        body: { percentOff: Number(c.percentOff), programId: c.programId, expiresAt: c.expiresAt, maxRedemptions: max, isActive: !c.isActive },
      })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not change the code.')
    },
    onSuccess: refresh,
  })
  const remove = useMutation({
    mutationFn: async () => {
      const { response } = await api.DELETE('/api/v1/studio/{tenantSlug}/discounts/{discountId}', path)
      if (!response.ok) throw new Error('Could not delete the code.')
    },
    onSuccess: refresh,
  })
  const error = toggle.error ?? remove.error

  return (
    <li className="space-y-2 rounded-2xl border bg-card p-4">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-mono text-lg font-bold tracking-wide">{c.code}</span>
        <Badge>−{Number(c.percentOff)}%</Badge>
        {!c.isActive ? <Badge variant="outline">Off</Badge> : expired ? <Badge variant="outline">Ended</Badge> : max !== null && uses >= max ? <Badge variant="outline">Used up</Badge> : null}
      </div>
      <p className="text-sm text-muted-foreground">
        {programTitle} · {uses} {uses === 1 ? 'use' : 'uses'}
        {max !== null && ` of ${max}`}
        {c.expiresAt && ` · ${expired ? 'ended' : 'until'} ${new Date(c.expiresAt).toLocaleDateString()}`}
      </p>
      <div className="flex flex-wrap gap-2">
        <Button size="sm" variant="outline" onClick={() => toggle.mutate()} disabled={toggle.isPending}>
          {c.isActive ? 'Switch off' : 'Switch on'}
        </Button>
        <Button size="sm" variant="ghost" onClick={() => remove.mutate()} disabled={remove.isPending} aria-label={`Delete the code ${c.code}`}>
          <Trash2 aria-hidden /> Delete
        </Button>
      </div>
      {error && (
        <Alert variant="destructive" role="alert">
          <AlertDescription>{error.message}</AlertDescription>
        </Alert>
      )}
    </li>
  )
}

function NewCode({ slug, programs }: { slug: string; programs: { id: string; title: string }[] }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const [code, setCode] = useState('')
  const [percent, setPercent] = useState('20')
  const [programId, setProgramId] = useState('')
  const [until, setUntil] = useState('')
  const [maxUses, setMaxUses] = useState('')

  const create = useMutation({
    mutationFn: async () => {
      const { error, response } = await api.POST('/api/v1/studio/{tenantSlug}/discounts', {
        params: { path: { tenantSlug: slug } },
        body: {
          code: code.trim(),
          percentOff: Number(percent),
          programId: programId || null,
          // The end of the chosen day, in the coach's time zone.
          expiresAt: until ? new Date(`${until}T23:59:59`).toISOString() : null,
          maxRedemptions: maxUses ? Number(maxUses) : null,
        },
      })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not create the code.')
    },
    onSuccess: () => {
      setCode('')
      setUntil('')
      setMaxUses('')
      return queryClient.invalidateQueries({ queryKey: ['discounts', slug] })
    },
  })

  return (
    <form
      aria-labelledby="new-code-heading"
      className="space-y-4 rounded-3xl border bg-card p-6"
      onSubmit={(e) => {
        e.preventDefault()
        create.mutate()
      }}
    >
      <h2 id="new-code-heading" className="text-lg font-bold">
        New code
      </h2>
      <Field id="code" label="Code" hint="3 to 20 letters, digits or dashes, for example SUMMER-20.">
        <Input
          id="code"
          required
          minLength={3}
          maxLength={20}
          pattern="[A-Za-z0-9\-]{3,20}"
          autoComplete="off"
          className="font-mono uppercase"
          value={code}
          onChange={(e) => setCode(e.target.value)}
        />
      </Field>
      <Field id="percent" label="Percent off" hint="From 5 to 90.">
        <Input id="percent" type="number" required min={5} max={90} value={percent} onChange={(e) => setPercent(e.target.value)} />
      </Field>
      <Field id="program" label="Applies to">
        <select id="program" className={selectClassName} value={programId} onChange={(e) => setProgramId(e.target.value)}>
          <option value="">All programs</option>
          {programs.map((p) => (
            <option key={p.id} value={p.id}>
              {p.title}
            </option>
          ))}
        </select>
      </Field>
      <Field id="until" label="Last day (optional)">
        <Input id="until" type="date" min={new Date().toISOString().slice(0, 10)} value={until} onChange={(e) => setUntil(e.target.value)} />
      </Field>
      <Field id="max-uses" label="Number of uses (optional)" hint="Leave empty for no limit.">
        <Input id="max-uses" type="number" min={1} value={maxUses} onChange={(e) => setMaxUses(e.target.value)} />
      </Field>
      {create.error && (
        <Alert variant="destructive" role="alert">
          <AlertDescription>{create.error.message}</AlertDescription>
        </Alert>
      )}
      <Button type="submit" className="w-full" disabled={create.isPending}>
        {create.isPending ? 'Creating…' : 'Create code'}
      </Button>
    </form>
  )
}
