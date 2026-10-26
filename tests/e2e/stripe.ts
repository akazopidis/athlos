// Stripe's API in test mode, for the spec that pays (design §15.1): with the stage's test key
// (E2E_STRIPE_SECRET_KEY, read from the stage's Key Vault by the workflow), the spec refunds its own
// test payment, so the next run pays again. Only a test-mode key is ever used: a live key is refused.
const key = process.env.E2E_STRIPE_SECRET_KEY || undefined

export const canRefund = !!key && /^(sk|rk)_test_/.test(key)

type Charge = { refunded: boolean }
type PaymentIntent = { id: string; latest_charge: Charge | null }
type Session = { id: string; payment_status: string; payment_intent: PaymentIntent | null }

async function stripe<T>(path: string, form?: Record<string, string>): Promise<T> {
  const response = await fetch(`https://api.stripe.com/v1/${path}`, {
    method: form ? 'POST' : 'GET',
    headers: {
      Authorization: `Bearer ${key}`,
      ...(form ? { 'Content-Type': 'application/x-www-form-urlencoded' } : {}),
    },
    body: form ? new URLSearchParams(form) : undefined,
  })
  if (!response.ok) throw new Error(`Stripe ${form ? 'POST' : 'GET'} /v1/${path.split('?')[0]}: ${response.status} ${await response.text()}`)
  return (await response.json()) as T
}

// Refunds in full every paid, not yet refunded payment of this customer for a program of this title
// (a line item named after the program, with or without a discount code). Stripe then sends
// charge.refunded, and Athlos refunds the order and removes the access as for any refund made at Stripe.
export async function refundTestPurchases(customerEmail: string, programTitle: string): Promise<number> {
  if (!canRefund) throw new Error('E2E_STRIPE_SECRET_KEY is not a Stripe test key')

  const sessions = await stripe<{ data: Session[] }>(
    `checkout/sessions?status=complete&limit=100&customer_details[email]=${encodeURIComponent(customerEmail)}&expand[]=data.payment_intent.latest_charge`,
  )
  let refunded = 0
  for (const session of sessions.data) {
    const payment = session.payment_intent
    if (session.payment_status !== 'paid' || !payment?.latest_charge || payment.latest_charge.refunded) continue
    const items = await stripe<{ data: { description: string }[] }>(`checkout/sessions/${session.id}/line_items?limit=100`)
    if (!items.data.some((item) => item.description === programTitle || item.description.startsWith(`${programTitle} (code `))) continue
    await stripe('refunds', { payment_intent: payment.id, reason: 'requested_by_customer' })
    refunded++
  }
  return refunded
}
