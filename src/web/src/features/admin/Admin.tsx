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
import { Label } from '@/components/ui/label'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Textarea } from '@/components/ui/textarea'
import { useApp } from '@/lib/AppContext'
import { formatMoney } from '@/lib/money'
import { problemTitle } from '@/lib/problem'
import { usePlatformAdmin } from '@/lib/usePlatformAdmin'
import { useTitle } from '@/lib/useTitle'

type Workspace = components['schemas']['ListTenantsItem']
type Status = components['schemas']['TenantStatus']
type Plan = components['schemas']['PlanName']
type Action = components['schemas']['AdminActionsItem']

const PageSize = 20
const MinReason = 3

// Every admin action states why (design §3.4): the reason goes with the change into the service's
// audit trail, and a suspended coach is told it. One field serves every action on the page, and is
// cleared after each one.
type Reason = { text: string; ready: boolean; clear: () => void }

// The platform admin (design §1, §3.4): every workspace, suspending and reactivating them, and
// assigning plans. Only for users with the Platform.Admin app role; tenancy-api enforces it.
export function Admin() {
  useTitle('Platform admin')
  const { auth } = useApp()
  const isAdmin = usePlatformAdmin()
  const [text, setText] = useState('')
  const reason: Reason = { text: text.trim(), ready: text.trim().length >= MinReason, clear: () => setText('') }

  if (!auth?.account || !isAdmin) {
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
      <Card>
        <CardContent className="space-y-2">
          <Label htmlFor="admin-reason">Reason for your next action</Label>
          <Textarea
            id="admin-reason"
            maxLength={500}
            rows={2}
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder="For example: sells programs copied from another coach"
          />
          <p className="text-xs text-muted-foreground">
            Every action below needs one. It is kept in the log with your admin id, and a coach whose workspace you suspend receives it by email.
          </p>
        </CardContent>
      </Card>
      <RefundRequests reason={reason} />
      <Payouts reason={reason} />
      <ReportedReviews reason={reason} />
      <Workspaces reason={reason} />
      <RecentActions />
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

// Coach shares whose transfer failed after payment (ordering-api's payout retries, design §2.2): they
// are retried with backoff; the admin can retry one now, for example once the coach fixed their
// Stripe account. Hidden while there are none.
// Reviews someone reported (design §2.4), one entry per review: hide it (it leaves the program's page
// and rating) or keep it. Either closes its reports.
function ReportedReviews({ reason }: { reason: Reason }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const reports = useQuery({
    queryKey: ['admin', 'review-reports'],
    queryFn: async () => (await api.GET('/api/v1/admin/reviews/reports')).data ?? [],
  })
  const decide = useMutation({
    mutationFn: async ({ reviewId, hide }: { reviewId: string; hide: boolean }) => {
      const params = { params: { path: { reviewId } }, body: { reason: reason.text } }
      const { error, response } = hide
        ? await api.POST('/api/v1/admin/reviews/{reviewId}/hide', params)
        : await api.POST('/api/v1/admin/reviews/{reviewId}/keep', params)
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not save the decision.')
    },
    onSuccess: reason.clear,
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['admin'] }),
  })

  if (!reports.data?.length) return null
  return (
    <Section title="Reported reviews">
      {decide.error && (
        <Alert variant="destructive">
          <AlertDescription>{decide.error.message}</AlertDescription>
        </Alert>
      )}
      <ul className="space-y-3">
        {reports.data.map((r) => (
          <li key={r.reviewId} className="space-y-2 rounded-2xl border bg-card p-4">
            <div className="flex flex-wrap items-center gap-2 text-sm">
              <span className="font-semibold">{r.authorName}</span>
              <span className="text-muted-foreground">
                {Number(r.rating)}/5 on {r.programTitle} (/c/{r.coachSlug})
              </span>
              <Badge variant="destructive" className="ml-auto">
                {Number(r.reports)} {Number(r.reports) === 1 ? 'report' : 'reports'}
              </Badge>
            </div>
            <p className="text-sm whitespace-pre-line">{r.comment ?? <span className="text-muted-foreground italic">No comment</span>}</p>
            <p className="text-xs text-muted-foreground">
              Reasons: {r.reasons.join(', ')}
              {r.notes.length > 0 && ` · Notes: ${r.notes.join(' / ')}`}
            </p>
            <div className="flex gap-2">
              <Button size="sm" variant="destructive" disabled={decide.isPending || !reason.ready} onClick={() => decide.mutate({ reviewId: r.reviewId, hide: true })}>
                Hide review
              </Button>
              <Button size="sm" variant="outline" disabled={decide.isPending || !reason.ready} onClick={() => decide.mutate({ reviewId: r.reviewId, hide: false })}>
                Keep it
              </Button>
            </div>
          </li>
        ))}
      </ul>
    </Section>
  )
}

// Refund requests (design §2.2): the customer asked within the refund window, and the coach has three
// working days to refund it. Past its due date the admin refunds it, keeping the refund promise; the
// coach's share is taken back when Stripe allows. Hidden while there are none.
function RefundRequests({ reason }: { reason: Reason }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const [now] = useState(() => Date.now())
  const requests = useQuery({
    queryKey: ['admin', 'refund-requests'],
    queryFn: async () => (await api.GET('/api/v1/admin/refund-requests')).data ?? [],
  })
  const refund = useMutation({
    mutationFn: async (orderId: string) => {
      const { error, response } = await api.POST('/api/v1/admin/refund-requests/{orderId}/refund', { params: { path: { orderId } }, body: { reason: reason.text } })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not refund it.')
    },
    onSuccess: reason.clear,
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['admin'] }),
  })

  if (!requests.data?.length) return null
  return (
    <Section title="Refund requests">
      <p className="text-sm text-muted-foreground">Customers asked within the refund window. Each coach has three working days; after that, refund it here.</p>
      {refund.error && (
        <Alert variant="destructive">
          <AlertDescription>{refund.error.message}</AlertDescription>
        </Alert>
      )}
      <ul className="divide-y rounded-2xl border bg-card">
        {requests.data.map((r) => {
          const due = new Date(r.dueAt).getTime() <= now
          return (
            <li key={r.orderId} className="flex flex-wrap items-center gap-x-4 gap-y-2 p-4 text-sm">
              <div className="min-w-0 flex-1 basis-48">
                <p className="truncate font-medium">{r.programTitle}</p>
                <p className="text-muted-foreground">
                  /c/{r.coachSlug} · asked {new Date(r.requestedAt).toLocaleDateString()} · {due ? 'overdue since' : 'due'} {new Date(r.dueAt).toLocaleString()}
                </p>
              </div>
              <span className="tabular-nums">{formatMoney(r.amount, r.currency)}</span>
              <Button size="sm" variant={due ? 'destructive' : 'outline'} disabled={!due || refund.isPending || !reason.ready} onClick={() => refund.mutate(r.orderId)}>
                Refund
              </Button>
            </li>
          )
        })}
      </ul>
    </Section>
  )
}

function Payouts({ reason }: { reason: Reason }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const payouts = useQuery({
    queryKey: ['admin', 'payouts'],
    queryFn: async () => (await api.GET('/api/v1/admin/payouts')).data ?? [],
  })
  const retry = useMutation({
    mutationFn: async (orderId: string) => {
      const { data, error } = await api.POST('/api/v1/admin/payouts/{orderId}/retry', { params: { path: { orderId } }, body: { reason: reason.text } })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not retry the payout.')
      reason.clear()
      if (!data.paid) throw new Error('Stripe still refused the transfer; it stays queued.')
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['admin'] }),
  })

  if (!payouts.data?.length) return null
  return (
    <Section title="Coach payouts waiting">
      <p className="text-sm text-muted-foreground">
        The customers paid and have their programs; these coaches' shares could not be transferred yet. They are retried automatically.
      </p>
      {retry.error && (
        <Alert variant="destructive">
          <AlertDescription>{retry.error.message}</AlertDescription>
        </Alert>
      )}
      <Card>
        <CardContent>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Coach</TableHead>
                <TableHead className="text-right">Share</TableHead>
                <TableHead className="hidden sm:table-cell">Last error</TableHead>
                <TableHead className="hidden md:table-cell">Next try</TableHead>
                <TableHead className="text-right">
                  <span className="sr-only">Retry</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {payouts.data.map((p) => (
                <TableRow key={p.orderId}>
                  <TableCell>
                    <span className="font-medium">/c/{p.coachSlug}</span>
                    <span className="block text-xs text-muted-foreground">{Number(p.attempts)} {Number(p.attempts) === 1 ? 'attempt' : 'attempts'}</span>
                  </TableCell>
                  <TableCell className="text-right tabular-nums">{formatMoney(p.amount, p.currency)}</TableCell>
                  <TableCell className="hidden font-mono text-xs sm:table-cell">{p.lastError ?? '—'}</TableCell>
                  <TableCell className="hidden text-muted-foreground md:table-cell">{new Date(p.nextAttemptAt).toLocaleString()}</TableCell>
                  <TableCell className="text-right">
                    <Button size="sm" variant="outline" disabled={retry.isPending || !reason.ready} onClick={() => retry.mutate(p.orderId)}>
                      Retry now
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>
    </Section>
  )
}

function Workspaces({ reason }: { reason: Reason }) {
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
      const { data, error } = await api.PUT('/api/v1/admin/tenants/{tenantId}/status', { params: { path: { tenantId: id } }, body: { status: next, reason: reason.text } })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not change the workspace.')
    },
    onSuccess: reason.clear,
    onSettled: refresh,
  })
  const setTenantPlan = useMutation({
    mutationFn: async ({ id, plan }: { id: string; plan: Plan }) => {
      const { data, error } = await api.PUT('/api/v1/admin/tenants/{tenantId}/plan', { params: { path: { tenantId: id } }, body: { plan, reason: reason.text } })
      if (!data) throw new Error(problemTitle(error) ?? 'Could not change the plan.')
    },
    onSuccess: reason.clear,
    onSettled: refresh,
  })
  const failure = setTenantStatus.error ?? setTenantPlan.error
  const busy = setTenantStatus.isPending || setTenantPlan.isPending || !reason.ready

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

const actionLabels: Record<Action['action'], string> = {
  WorkspaceSuspended: 'Suspended',
  WorkspaceReactivated: 'Reactivated',
  PlanChanged: 'Plan changed',
  ReviewHidden: 'Review hidden',
  ReviewKept: 'Review kept',
  PayoutRetried: 'Payout retried',
  RefundIssued: 'Refund issued',
}

// The audit trail (design §3.4): each service keeps the admin actions it carried out (tenancy:
// workspaces and plans, enrollment: reviews, ordering: payouts); the page shows them together, newest first.
function RecentActions() {
  const { api } = useApp()
  const actions = useQuery({
    queryKey: ['admin', 'actions'],
    queryFn: async () => {
      const [workspaces, reviews, payouts] = await Promise.all([
        api.GET('/api/v1/admin/actions'),
        api.GET('/api/v1/admin/reviews/actions'),
        api.GET('/api/v1/admin/payouts/actions'),
      ])
      // Ordering's log holds payout retries and refunds of refund requests.
      return [...(workspaces.data ?? []), ...(reviews.data ?? []), ...(payouts.data ?? [])].sort((a, b) => b.at.localeCompare(a.at)).slice(0, 50)
    },
  })

  if (!actions.data?.length) return null
  return (
    <Section title="Recent actions">
      <ul className="divide-y rounded-2xl border bg-card">
        {actions.data.map((a) => (
          <li key={a.id} className="space-y-1 p-4 text-sm">
            <div className="flex flex-wrap items-center gap-2">
              <Badge variant="outline">{actionLabels[a.action]}</Badge>
              <span className="font-medium">{a.subject}</span>
              {a.detail && <span className="text-muted-foreground">{a.detail}</span>}
              <time className="ml-auto text-xs text-muted-foreground" dateTime={a.at}>
                {new Date(a.at).toLocaleString()}
              </time>
            </div>
            <p className="whitespace-pre-line">{a.reason}</p>
            <p className="text-xs text-muted-foreground">Admin {a.adminId.slice(0, 8)}</p>
          </li>
        ))}
      </ul>
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
