import { useQuery } from '@tanstack/react-query'
import { ArrowLeft, Printer } from 'lucide-react'
import { Link, useParams } from 'react-router'
import { Logo } from '@/components/logo'
import { Loading, Notice } from '@/components/page'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { formatMoney } from '@/lib/money'
import { useTitle } from '@/lib/useTitle'

// The receipt of one checkout (design §5.4, "/me/orders/{checkoutId}/receipt"): what was bought from
// which coach, the price, any discount, the total paid and anything refunded. "Print or save as PDF"
// uses the browser's print dialog; the site's header and footer are left out of the printout.
export function Receipt() {
  const { checkoutId = '' } = useParams()
  const { api, auth } = useApp()
  const signedIn = Boolean(auth?.account)
  useTitle('Receipt')

  const receipt = useQuery({
    queryKey: ['receipt', checkoutId],
    queryFn: async () => (await api.GET('/api/v1/me/orders/receipts/{checkoutId}', { params: { path: { checkoutId } } })).data ?? null,
    enabled: signedIn,
  })

  if (!signedIn) return <Notice title="Sign in to see your receipt" />
  if (receipt.isPending) return <Loading lines={6} />
  if (!receipt.data) {
    return (
      <Notice
        title="Receipt not found"
        action={
          <Button asChild variant="outline">
            <Link to="/me/orders">Orders</Link>
          </Button>
        }
      >
        A receipt is ready once the payment is confirmed.
      </Notice>
    )
  }

  const r = receipt.data
  const money = (amount: number | string) => formatMoney(amount, r.currency)
  return (
    <div className="mx-auto max-w-2xl space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3 print:hidden">
        <Button asChild variant="ghost" size="sm" className="-ml-3">
          <Link to="/me/orders">
            <ArrowLeft aria-hidden /> Orders
          </Link>
        </Button>
        <Button variant="outline" size="sm" onClick={() => window.print()}>
          <Printer aria-hidden /> Print or save as PDF
        </Button>
      </div>
      <article className="space-y-8 rounded-3xl border bg-card p-6 sm:p-10 print:border-0 print:p-0">
        <header className="flex flex-wrap items-start justify-between gap-4">
          <Logo />
          <div className="text-right text-sm">
            <h1 className="text-2xl font-black tracking-tight">Receipt</h1>
            <p className="text-muted-foreground">{new Date(r.paidAt).toLocaleDateString(undefined, { dateStyle: 'long' })}</p>
            <p className="font-mono text-xs text-muted-foreground">{r.checkoutId}</p>
          </div>
        </header>
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b text-left text-muted-foreground">
              <th className="py-2 font-medium">Program</th>
              <th className="py-2 text-right font-medium">Price</th>
              <th className="py-2 text-right font-medium">Paid</th>
            </tr>
          </thead>
          <tbody>
            {r.lines.map((l) => (
              <tr key={l.orderId} className="border-b align-top">
                <td className="py-3">
                  <span className="block font-medium">{l.programTitle}</span>
                  <span className="block text-muted-foreground">by {l.coachSlug}</span>
                  {l.discountCode && (
                    <span className="block text-muted-foreground">
                      Code {l.discountCode}: −{money(l.discountAmount)}
                    </span>
                  )}
                  {l.status === 'Refunded' && (
                    <Badge variant="secondary" className="mt-1">
                      Refunded{l.refundedAt ? ` ${new Date(l.refundedAt).toLocaleDateString()}` : ''}
                    </Badge>
                  )}
                </td>
                <td className="py-3 text-right tabular-nums">{money(l.price)}</td>
                <td className="py-3 text-right font-medium tabular-nums">{money(l.amount)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <dl className="ml-auto max-w-xs space-y-1.5 text-sm">
          <div className="flex justify-between">
            <dt className="text-muted-foreground">Subtotal</dt>
            <dd className="tabular-nums">{money(r.subtotal)}</dd>
          </div>
          {Number(r.discount) > 0 && (
            <div className="flex justify-between">
              <dt className="text-muted-foreground">Discounts</dt>
              <dd className="tabular-nums">−{money(r.discount)}</dd>
            </div>
          )}
          <div className="flex justify-between border-t pt-2 text-base font-bold">
            <dt>Total paid</dt>
            <dd className="tabular-nums">{money(r.total)}</dd>
          </div>
          {Number(r.refunded) > 0 && (
            <div className="flex justify-between">
              <dt className="text-muted-foreground">Refunded</dt>
              <dd className="tabular-nums">−{money(r.refunded)}</dd>
            </div>
          )}
        </dl>
        <p className="text-xs text-muted-foreground">
          Paid by card through Stripe to Athlos, which passes each coach their share. This receipt is not a tax invoice.
        </p>
      </article>
    </div>
  )
}
