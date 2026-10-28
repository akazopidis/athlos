import { useMutation, useQuery } from '@tanstack/react-query'
import { CircleCheck, ExternalLink } from 'lucide-react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { formatMoney } from '@/lib/money'
import { problemTitle } from '@/lib/problem'

const Statuses: Record<string, string> = { paid: 'Paid', pending: 'On its way', in_transit: 'On its way', canceled: 'Cancelled', failed: 'Failed' }

// Payouts once Stripe has the coach's details (design §2.2, backlog item 42). The owner sees the
// balance and the latest payouts to their bank, read from Stripe, and opens the Stripe Express
// dashboard for bank details, the payout schedule and tax forms. Staff see only that payouts work:
// the money goes to the owner's bank.
export function StudioPayouts({ slug, isOwner }: { slug: string; isOwner: boolean }) {
  const { api } = useApp()
  const params = { params: { path: { tenantSlug: slug } } }

  const history = useQuery({
    queryKey: ['payout-history', slug],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/studio/{tenantSlug}/payouts/history', params)
      if (!data) throw new Error(problemTitle(error) ?? 'Could not read your payouts from Stripe just now.')
      return data
    },
    enabled: isOwner,
  })

  // Stripe's link works once and for a few minutes, so it is asked for on the click.
  const dashboard = useMutation({
    mutationFn: async () => {
      const { data, error } = await api.POST('/api/v1/studio/{tenantSlug}/payouts/dashboard', params)
      if (!data) throw new Error(problemTitle(error) ?? 'Could not open the Stripe dashboard.')
      return data
    },
    onSuccess: ({ url }) => window.location.assign(url),
  })

  const amounts = (list: { amount: number | string; currency: string }[]) =>
    list.length ? list.map((m) => formatMoney(m.amount, m.currency)).join(' + ') : formatMoney(0, 'EUR')

  return (
    <div className="space-y-4">
      <Alert>
        <CircleCheck aria-hidden />
        <AlertDescription>Payouts are set up. You can publish programs.</AlertDescription>
      </Alert>
      {isOwner && (
        <>
          {history.data && (
            <div className="space-y-3 rounded-2xl border bg-card p-4">
              <dl className="grid grid-cols-2 gap-4 text-sm">
                <div>
                  <dt className="text-muted-foreground">Ready to pay out</dt>
                  <dd className="text-lg font-semibold tabular-nums">{amounts(history.data.available)}</dd>
                </div>
                <div>
                  <dt className="text-muted-foreground">Still arriving</dt>
                  <dd className="text-lg font-semibold tabular-nums">{amounts(history.data.pending)}</dd>
                </div>
              </dl>
              {history.data.payouts.length > 0 ? (
                <div className="space-y-1">
                  <h3 className="text-sm font-medium">Latest payouts to your bank</h3>
                  <ul className="divide-y rounded-lg border text-sm">
                    {history.data.payouts.map((p, i) => (
                      <li key={`${p.arrivalDate}-${i}`} className="flex items-center justify-between gap-3 px-3 py-2">
                        <span>{new Date(p.arrivalDate).toLocaleDateString()}</span>
                        <span className="text-muted-foreground">{Statuses[p.status] ?? p.status}</span>
                        <span className="font-medium tabular-nums">{formatMoney(p.amount, p.currency)}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              ) : (
                <p className="text-sm text-muted-foreground">No payouts yet. Stripe pays out your share of sales on its schedule.</p>
              )}
            </div>
          )}
          {history.error && <p className="text-sm text-muted-foreground">{history.error.message}</p>}
          <Button variant="outline" onClick={() => dashboard.mutate()} disabled={dashboard.isPending}>
            <ExternalLink aria-hidden /> {dashboard.isPending ? 'Opening…' : 'Open Stripe dashboard'}
          </Button>
          {dashboard.error && (
            <Alert variant="destructive" role="alert">
              <AlertDescription>{dashboard.error.message}</AlertDescription>
            </Alert>
          )}
        </>
      )}
    </div>
  )
}
