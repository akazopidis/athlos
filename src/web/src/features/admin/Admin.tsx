import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Search, ShieldAlert } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import type { components } from '@/api/tenancy'
import { selectClassName } from '@/components/field'
import { Empty, Loading, Notice, PageHeader, Section } from '@/components/page'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'
import { usePlatformAdmin } from '@/lib/usePlatformAdmin'
import { useTitle } from '@/lib/useTitle'

type Workspace = components['schemas']['ListTenantsItem']
type Status = components['schemas']['TenantStatus']
type Plan = components['schemas']['PlanName']

const PageSize = 20

// The platform admin (design §1, §3.4): every workspace, suspending and reactivating them, and
// assigning plans. Only for users with the Platform.Admin app role; tenancy-api enforces it.
export function Admin() {
  useTitle('Platform admin')
  const { msal } = useApp()
  const isAdmin = usePlatformAdmin()

  if (!msal?.getActiveAccount() || !isAdmin) {
    return (
      <Notice title="Platform admins only" action={<Link to="/" className="font-medium underline underline-offset-4">Back to the marketplace</Link>}>
        This page is for the people who run Athlos.
      </Notice>
    )
  }

  return (
    <div className="space-y-12">
      <PageHeader eyebrow="Platform admin" title="Workspaces" description="Every coach on Athlos. Suspend a workspace to take it off the marketplace; its customers keep what they bought." />
      <Metrics />
      <Workspaces />
    </div>
  )
}

function Metrics() {
  const { api } = useApp()
  const metrics = useQuery({
    queryKey: ['admin', 'metrics'],
    queryFn: async () => (await api.GET('/api/v1/admin/metrics')).data ?? null,
  })

  if (metrics.isPending) return <Loading lines={2} />
  if (!metrics.data) return null
  const m = metrics.data
  const cards = [
    { label: 'Active workspaces', value: m.active, detail: `${m.activeFree} Free · ${m.activePro} Pro` },
    { label: 'Ready to sell', value: m.readyToSell, detail: 'Active, Stripe onboarding done' },
    { label: 'New in 30 days', value: m.newLast30Days, detail: `${m.workspaces} in total` },
    { label: 'Suspended', value: m.suspended, detail: `${m.deleted} deleted` },
  ]

  return (
    <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
      {cards.map((c) => (
        <Card key={c.label}>
          <CardHeader>
            <CardDescription>{c.label}</CardDescription>
            <CardTitle className="font-heading text-4xl font-black tabular-nums">{c.value}</CardTitle>
            <p className="text-xs text-muted-foreground">{c.detail}</p>
          </CardHeader>
        </Card>
      ))}
    </div>
  )
}

function Workspaces() {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState<Status | ''>('')
  const [page, setPage] = useState(1)

  const workspaces = useQuery({
    queryKey: ['admin', 'tenants', query, status, page],
    queryFn: async () =>
      (await api.GET('/api/v1/admin/tenants', { params: { query: { Q: query || undefined, Status: status || undefined, Page: page, PageSize } } })).data ?? null,
    placeholderData: keepPreviousData,
  })

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['admin'] })
  const setTenantStatus = useMutation({
    mutationFn: async ({ id, next }: { id: string; next: Status }) => {
      const { data, error } = await api.PUT('/api/v1/admin/tenants/{tenantId}/status', { params: { path: { tenantId: id } }, body: { status: next } })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not change the workspace.')
    },
    onSettled: refresh,
  })
  const setTenantPlan = useMutation({
    mutationFn: async ({ id, plan }: { id: string; plan: Plan }) => {
      const { data, error } = await api.PUT('/api/v1/admin/tenants/{tenantId}/plan', { params: { path: { tenantId: id } }, body: { plan } })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not change the plan.')
    },
    onSettled: refresh,
  })
  const failure = setTenantStatus.error ?? setTenantPlan.error
  const busy = setTenantStatus.isPending || setTenantPlan.isPending

  const total = Number(workspaces.data?.totalCount ?? 0)
  const pages = Math.max(1, Math.ceil(total / PageSize))

  return (
    <Section title="All workspaces">
      <form
        role="search"
        className="flex flex-col gap-3 sm:flex-row"
        onSubmit={(e) => {
          e.preventDefault()
          setQuery(String(new FormData(e.currentTarget).get('q') ?? '').trim())
          setPage(1)
        }}
      >
        <div className="relative flex-1">
          <Search className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
          <Input name="q" aria-label="Search by name or address" placeholder="Search by name or address" className="pl-9" defaultValue={query} />
        </div>
        <select
          aria-label="Status"
          className={`${selectClassName} sm:w-44`}
          value={status}
          onChange={(e) => {
            setStatus(e.target.value as Status | '')
            setPage(1)
          }}
        >
          <option value="">Any status</option>
          <option value="Active">Active</option>
          <option value="Suspended">Suspended</option>
          <option value="Deleted">Deleted</option>
        </select>
        <Button type="submit" variant="outline">
          Search
        </Button>
      </form>

      {failure && (
        <Alert variant="destructive">
          <AlertDescription>{failure.message}</AlertDescription>
        </Alert>
      )}

      {workspaces.isPending ? (
        <Loading />
      ) : !workspaces.data?.items.length ? (
        <Empty icon={<ShieldAlert />}>No workspaces match.</Empty>
      ) : (
        <Card>
          <CardContent>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Workspace</TableHead>
                  <TableHead className="hidden sm:table-cell">Status</TableHead>
                  <TableHead>Plan</TableHead>
                  <TableHead className="hidden md:table-cell">Opened</TableHead>
                  <TableHead className="text-right">
                    <span className="sr-only">Actions</span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {workspaces.data.items.map((w) => (
                  <WorkspaceRow
                    key={w.id}
                    workspace={w}
                    busy={busy}
                    onStatus={(next) => setTenantStatus.mutate({ id: w.id, next })}
                    onPlan={(plan) => setTenantPlan.mutate({ id: w.id, plan })}
                  />
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}

      {pages > 1 && (
        <nav className="flex items-center justify-center gap-4" aria-label="Pages">
          <Button variant="outline" disabled={page <= 1} onClick={() => setPage(page - 1)}>
            Previous
          </Button>
          <span className="text-sm text-muted-foreground">
            Page {page} of {pages}
          </span>
          <Button variant="outline" disabled={page >= pages} onClick={() => setPage(page + 1)}>
            Next
          </Button>
        </nav>
      )}
    </Section>
  )
}

function WorkspaceRow({ workspace: w, busy, onStatus, onPlan }: { workspace: Workspace; busy: boolean; onStatus: (next: Status) => void; onPlan: (plan: Plan) => void }) {
  const deleted = w.status === 'Deleted'
  return (
    <TableRow>
      <TableCell className="max-w-56">
        {w.status === 'Active' ? (
          <Link to={`/c/${w.slug}`} className="block truncate font-medium underline-offset-4 hover:underline">
            {w.displayName}
          </Link>
        ) : (
          <span className="block truncate font-medium">{w.displayName}</span>
        )}
        <span className="block truncate text-xs text-muted-foreground">/c/{w.slug}</span>
      </TableCell>
      <TableCell className="hidden sm:table-cell">
        <Badge variant={w.status === 'Suspended' ? 'destructive' : w.status === 'Deleted' ? 'secondary' : 'outline'}>{w.status}</Badge>
        {w.status === 'Active' && !w.readyToSell && <span className="ml-2 text-xs text-muted-foreground">payouts pending</span>}
      </TableCell>
      <TableCell>
        <select
          aria-label={`Plan of ${w.displayName}`}
          className={`${selectClassName} h-9 w-24`}
          value={w.plan}
          disabled={busy || deleted}
          onChange={(e) => onPlan(e.target.value as Plan)}
        >
          <option value="Free">Free</option>
          <option value="Pro">Pro</option>
        </select>
      </TableCell>
      <TableCell className="hidden text-muted-foreground md:table-cell">{new Date(w.createdAt).toLocaleDateString()}</TableCell>
      <TableCell className="text-right">
        {!deleted && (
          <Button
            size="sm"
            variant={w.status === 'Active' ? 'destructive' : 'outline'}
            disabled={busy}
            onClick={() => onStatus(w.status === 'Active' ? 'Suspended' : 'Active')}
          >
            {w.status === 'Active' ? 'Suspend' : 'Reactivate'}
          </Button>
        )}
      </TableCell>
    </TableRow>
  )
}
