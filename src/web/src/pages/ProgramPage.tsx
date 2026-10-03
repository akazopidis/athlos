import { useMutation, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { useApp } from '../AppContext.tsx'
import { formatMoney } from '../money.ts'
import { leaveTo } from '../navigation.ts'
import { problemTitle } from '../problem.ts'

// A published program's public page with its Buy button. Buying sends the customer to Stripe
// Checkout; Stripe returns them to /me (paid) or to the storefront (cancelled).
export function ProgramPage() {
  const { slug = '', programSlug = '' } = useParams()
  const { api, msal, config } = useApp()
  const signedIn = Boolean(msal?.getActiveAccount())

  // One key per visit: a double click or a retry reuses the same order and checkout page (design §7.1).
  const [idempotencyKey] = useState(() => crypto.randomUUID())

  const program = useQuery({
    queryKey: ['program', slug, programSlug],
    queryFn: async () =>
      (
        await api.GET('/api/v1/marketplace/coaches/{coachSlug}/programs/{programSlug}', {
          params: { path: { coachSlug: slug, programSlug } },
        })
      ).data ?? null,
  })

  const buy = useMutation({
    mutationFn: async (programId: string) => {
      const { data, error, response } = await api.POST('/api/v1/orders/checkout', {
        body: { programId },
        params: { header: { 'Idempotency-Key': idempotencyKey } },
      })
      if (data) return data
      if (response.status === 401) throw new Error('Sign in to buy this program.')
      if (response.status === 404) throw new Error('This program is no longer for sale.')
      if (response.status === 503) throw new Error('Payments are not available yet in this environment.')
      throw new Error(problemTitle(error) ?? 'Could not start checkout.')
    },
    onSuccess: ({ checkoutUrl }) => leaveTo(checkoutUrl),
  })

  if (program.isPending) return <p>Loading…</p>
  if (!program.data) return <p>Program not found.</p>

  const p = program.data
  return (
    <article className="program">
      <p>
        <Link to={`/c/${p.coachSlug}`}>← All programs by this coach</Link>
      </p>
      {p.coverImageUrl && <img src={p.coverImageUrl} alt="" className="cover" />}
      <h1>{p.title}</h1>
      <p>
        {p.level} · {p.durationWeeks} weeks
      </p>
      <p className="description">{p.description}</p>
      <p className="price">{formatMoney(p.priceAmount, p.currency)}</p>
      {signedIn ? (
        <button type="button" onClick={() => buy.mutate(p.id)} disabled={buy.isPending || buy.isSuccess}>
          {buy.isPending || buy.isSuccess ? 'Opening checkout…' : 'Buy'}
        </button>
      ) : msal ? (
        <button type="button" onClick={() => msal.loginRedirect({ scopes: [config.apiScope!] })}>
          Sign in to buy
        </button>
      ) : (
        <p>Sign-in is not available in this environment.</p>
      )}
      {buy.error && <p role="alert">{buy.error.message}</p>}
    </article>
  )
}
