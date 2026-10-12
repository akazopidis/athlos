import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import type { components } from '@/api/ordering'
import { Empty, Loading } from '@/components/page'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useApp } from '@/lib/AppContext'
import { formatMoney } from '@/lib/money'
import { problemTitle } from '@/lib/problem'
import { useStudio } from './useStudio.ts'

type Sale = components['schemas']['OrderListsSale']

const PageSize = 20

// The coach's sales (ordering-api's paid orders of this workspace, design §8 "/studio": sales).
// The customer's address is shown to the coach here (and only here), so they can reach their buyers.
// The owner can refund a sale (design §2.2): the customer is paid back, the coach's share is taken
// back, and the customer loses access to the program. Refunded sales stay listed, marked.
export function StudioSales() {
  const { slug, isOwner } = useStudio()
  const { api } = useApp()
  const queryClient = useQueryClient()
  const [page, setPage] = useState(1)
  const [confirming, setConfirming] = useState<string | null>(null)

  const sales = useQuery({
    queryKey: ['sales', slug, page],
    queryFn: async () =>
      (await api.GET('/api/v1/studio/{tenantSlug}/sales', { params: { path: { tenantSlug: slug }, query: { Page: page, PageSize } } })).data ?? null,
    placeholderData: keepPreviousData,
  })

  const refund = useMutation({
    mutationFn: async (orderId: string) => {
      const { error, response } = await api.POST('/api/v1/studio/{tenantSlug}/sales/{orderId}/refund', {
        params: { path: { tenantSlug: slug, orderId } },
      })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not refund the sale.')
    },
    onSettled: () => {
      setConfirming(null)
      return queryClient.invalidateQueries({ queryKey: ['sales', slug] })
    },
  })

  if (sales.isPending) return <Loading />
  if (!sales.data?.items.length) return <Empty title="No sales yet">They appear here once a customer pays.</Empty>

  const total = Number(sales.data.totalCount)
  const pages = Math.max(1, Math.ceil(total / PageSize))
  // Per currency: programs may be priced in different currencies. Refunded sales earn nothing.
  const earnings = new Map<string, number>()
  for (const s of sales.data.items.filter((s) => s.status === 'Paid')) {
    earnings.set(s.currency, (earnings.get(s.currency) ?? 0) + Number(s.amount) - Number(s.platformFee))
  }

  return (
    <div className="space-y-4">
      <div className="grid gap-4 sm:grid-cols-2">
        <Card>
          <CardHeader>
            <CardDescription>Sales</CardDescription>
            <CardTitle className="font-heading text-4xl font-black tabular-nums">{total}</CardTitle>
          </CardHeader>
        </Card>
        <Card>
          <CardHeader>
            <CardDescription>Your share{pages > 1 ? ' (this page)' : ''}, after the platform fee</CardDescription>
            <CardTitle className="font-heading text-4xl font-black tabular-nums">
              {[...earnings].map(([currency, amount]) => formatMoney(amount, currency)).join(' + ') || formatMoney(0, 'EUR')}
            </CardTitle>
          </CardHeader>
        </Card>
      </div>
      {refund.error && (
        <Alert variant="destructive" role="alert">
          <AlertDescription>{refund.error.message}</AlertDescription>
        </Alert>
      )}
      <Card>
        <CardContent>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Date</TableHead>
                <TableHead>Program</TableHead>
                <TableHead className="hidden md:table-cell">Customer</TableHead>
                <TableHead className="hidden text-right sm:table-cell">Price</TableHead>
                <TableHead className="text-right">Your share</TableHead>
                {isOwner && (
                  <TableHead className="text-right">
                    <span className="sr-only">Refund</span>
                  </TableHead>
                )}
              </TableRow>
            </TableHeader>
            <TableBody>
              {sales.data.items.map((s) => (
                <SaleRow
                  key={s.id}
                  sale={s}
                  canRefund={isOwner}
                  confirming={confirming === s.id}
                  busy={refund.isPending}
                  onAsk={() => setConfirming(s.id)}
                  onCancel={() => setConfirming(null)}
                  onRefund={() => refund.mutate(s.id)}
                />
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>
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
    </div>
  )
}

function SaleRow({
  sale: s,
  canRefund,
  confirming,
  busy,
  onAsk,
  onCancel,
  onRefund,
}: {
  sale: Sale
  canRefund: boolean
  confirming: boolean
  busy: boolean
  onAsk: () => void
  onCancel: () => void
  onRefund: () => void
}) {
  const refunded = s.status === 'Refunded'
  return (
    <TableRow className={refunded ? 'text-muted-foreground' : undefined}>
      <TableCell>{new Date(s.paidAt).toLocaleDateString()}</TableCell>
      <TableCell className="max-w-48">
        <span className="block truncate">{s.programTitle}</span>
        {refunded && <Badge variant="secondary">Refunded</Badge>}
      </TableCell>
      <TableCell className="hidden text-muted-foreground md:table-cell">{s.customerEmail}</TableCell>
      <TableCell className="hidden text-right tabular-nums sm:table-cell">{formatMoney(s.amount, s.currency)}</TableCell>
      <TableCell className={refunded ? 'text-right tabular-nums line-through' : 'text-right font-medium tabular-nums'}>
        {formatMoney(Number(s.amount) - Number(s.platformFee), s.currency)}
      </TableCell>
      {canRefund && (
        <TableCell className="text-right">
          {refunded ? null : confirming ? (
            <span className="inline-flex gap-1">
              <Button size="sm" variant="destructive" disabled={busy} onClick={onRefund}>
                {busy ? 'Refunding…' : `Refund ${formatMoney(s.amount, s.currency)}`}
              </Button>
              <Button size="sm" variant="ghost" disabled={busy} onClick={onCancel}>
                Keep
              </Button>
            </span>
          ) : (
            <Button size="sm" variant="ghost" onClick={onAsk} aria-label={`Refund ${s.programTitle}`}>
              Refund
            </Button>
          )}
        </TableCell>
      )}
    </TableRow>
  )
}
