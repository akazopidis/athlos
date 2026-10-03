import { useMutation, useQuery } from '@tanstack/react-query'
import { ArrowLeft } from 'lucide-react'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Loading } from '@/components/page'
import { ProgramCover } from '@/components/program-cover'
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

  if (program.isPending) return <Loading lines={5} />
  if (!program.data) return <p>Program not found.</p>

  const p = program.data
  return (
    <article className="space-y-6">
      <Button asChild variant="ghost" size="sm" className="-ml-2">
        <Link to={`/c/${p.coachSlug}`}>
          <ArrowLeft aria-hidden /> All programs by this coach
        </Link>
      </Button>
      <div className="grid gap-8 md:grid-cols-[3fr_2fr]">
        <div className="space-y-4">
          <ProgramCover url={p.coverImageUrl} className="rounded-xl" />
          <h1 className="text-3xl font-semibold tracking-tight">{p.title}</h1>
          <div className="flex gap-2">
            <Badge variant="secondary">{p.level}</Badge>
            <Badge variant="outline">{Number(p.durationWeeks)} weeks</Badge>
          </div>
          <p className="whitespace-pre-line text-muted-foreground">{p.description}</p>
        </div>
        <Card className="h-fit">
          <CardContent className="space-y-4">
            <p className="text-3xl font-semibold">{formatMoney(p.priceAmount, p.currency)}</p>
            <p className="text-sm text-muted-foreground">One payment. All workouts, yours to keep.</p>
            {signedIn ? (
              <Button className="w-full" size="lg" onClick={() => buy.mutate(p.id)} disabled={buy.isPending || buy.isSuccess}>
                {buy.isPending || buy.isSuccess ? 'Opening checkout…' : 'Buy'}
              </Button>
            ) : msal ? (
              <Button className="w-full" size="lg" onClick={() => msal.loginRedirect({ scopes: [config.apiScope!] })}>
                Sign in to buy
              </Button>
            ) : (
              <p className="text-sm">Sign-in is not available in this environment.</p>
            )}
            {buy.error && (
              <Alert variant="destructive" role="alert">
                <AlertDescription>{buy.error.message}</AlertDescription>
              </Alert>
            )}
          </CardContent>
        </Card>
      </div>
    </article>
  )
}
