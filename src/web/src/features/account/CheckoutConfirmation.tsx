import { CircleCheck, Loader2 } from 'lucide-react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { useMyOrders } from './useMyOrders.ts'

// What Stripe returned the customer from (?checkout=success&id=, on "/me"): confirming while the
// webhook has not arrived yet, then confirmed.
export function CheckoutConfirmation() {
  const { returned } = useMyOrders()
  if (returned.length === 0) return null

  const pending = returned.some((o) => o.status === 'Pending')
  const titles = returned.map((o) => o.programTitle).join(', ')
  return pending ? (
    <Alert>
      <Loader2 className="animate-spin" aria-hidden />
      <AlertDescription>Payment received. Confirming your {returned.length === 1 ? 'order' : 'orders'}…</AlertDescription>
    </Alert>
  ) : (
    <Alert>
      <CircleCheck aria-hidden />
      <AlertDescription>Thank you! Your purchase of {titles} is confirmed.</AlertDescription>
    </Alert>
  )
}
