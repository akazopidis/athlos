import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Empty, Loading } from '@/components/page'
import { useApp } from '@/lib/AppContext'
import { formatMoney } from '@/lib/money'
import { useStudio } from './useStudio.ts'

const PageSize = 20

// The coach's sales (ordering-api's paid orders of this workspace, design §8 "/studio": sales).
// The customer's address is shown to the coach here (and only here), so they can reach their buyers.
export function StudioSales() {
  const { slug } = useStudio()
  const { api } = useApp()
  const [page, setPage] = useState(1)

  const sales = useQuery({
    queryKey: ['sales', slug, page],
    queryFn: async () =>
      (await api.GET('/api/v1/studio/{tenantSlug}/sales', { params: { path: { tenantSlug: slug }, query: { Page: page, PageSize } } })).data ?? null,
    placeholderData: keepPreviousData,
  })

  if (sales.isPending) return <Loading />
  if (!sales.data?.items.length) return <Empty title="No sales yet">They appear here once a customer pays.</Empty>

  const total = Number(sales.data.totalCount)
  const pages = Math.max(1, Math.ceil(total / PageSize))
  // Per currency: programs may be priced in different currencies.
  const earnings = new Map<string, number>()
  for (const s of sales.data.items) {
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
              {[...earnings].map(([currency, amount]) => formatMoney(amount, currency)).join(' + ')}
            </CardTitle>
          </CardHeader>
        </Card>
      </div>
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
              </TableRow>
            </TableHeader>
            <TableBody>
              {sales.data.items.map((s) => (
                <TableRow key={s.id}>
                  <TableCell>{new Date(s.paidAt).toLocaleDateString()}</TableCell>
                  <TableCell className="max-w-48 truncate">{s.programTitle}</TableCell>
                  <TableCell className="hidden text-muted-foreground md:table-cell">{s.customerEmail}</TableCell>
                  <TableCell className="hidden text-right tabular-nums sm:table-cell">{formatMoney(s.amount, s.currency)}</TableCell>
                  <TableCell className="text-right font-medium tabular-nums">{formatMoney(Number(s.amount) - Number(s.platformFee), s.currency)}</TableCell>
                </TableRow>
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
