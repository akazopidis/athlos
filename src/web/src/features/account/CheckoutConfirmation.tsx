import { CircleCheck, Loader2 } from 'lucide-react'
import { Link } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { formatMoney } from '@/lib/money'
import { useMyOrders } from './useMyOrders.ts'

// What Stripe returned the customer from (?checkout=success&id=, on "/me"): confirming while the
// webhook has not arrived yet, then confirmed.
export function CheckoutConfirmation() {
  const { returned } = useMyOrders()
  if (returned.length === 0) return null

  const pending = returned.some((o) => o.status === 'Pending')
  const titles = returned.map((o) => o.programTitle).join(', ')
  const saved = returned.reduce((sum, o) => sum + Number(o.discountAmount ?? 0), 0)
  const checkoutId = returned[0]?.checkoutId
  return pending ? (
    <Alert>
      <Loader2 className="animate-spin" aria-hidden />
      <AlertDescription>Payment received. Confirming your {returned.length === 1 ? 'order' : 'orders'}…</AlertDescription>
    </Alert>
  ) : (
    <Alert>
      <CircleCheck aria-hidden />
      <AlertDescription>
        Thank you! Your purchase of {titles} is confirmed.
        {saved > 0 && ` You saved ${formatMoney(saved, returned[0]!.currency)}.`}
        {checkoutId && (
          <>
            {' '}
            <Link to={`/me/orders/${checkoutId}/receipt`} className="font-medium underline underline-offset-4">
              See your receipt
            </Link>
          </>
        )}
      </AlertDescription>
    </Alert>
  )
}
