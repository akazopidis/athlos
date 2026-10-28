import { useMutation } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { useApp } from '@/lib/AppContext'
import { useTitle } from '@/lib/useTitle'

const topics: Record<string, string> = {
  programUpdates: 'new workouts in your programs',
  reviewReplies: 'replies to your reviews',
  priceDrops: 'lower prices on programs you saved',
}

// The unsubscribe link in an optional email: /unsubscribe#{token} (design §5.3). Like an invite link,
// the token is in the fragment, which the browser never sends to a server; the page posts it. No
// sign-in: the token names the customer and the email. It waits for a click, so a mail scanner that
// opens links unsubscribes nobody.
export function Unsubscribe() {
  const { api } = useApp()
  // Read during render, cleared from the address bar only once the page is on screen: under Suspense a
  // first render can be thrown away and the initializer run again, which must still find the token.
  const [token] = useState(() => window.location.hash.slice(1))
  useEffect(() => {
    if (window.location.hash) window.history.replaceState(null, '', window.location.pathname)
  }, [])
  useTitle('Unsubscribe')

  const unsubscribe = useMutation({
    mutationFn: async () => {
      const { data, response } = await api.POST('/api/v1/email-preferences/unsubscribe', { body: { token } })
      if (response.status === 404) {
        throw new Error('This link no longer works. Sign in and choose your emails in your account settings instead.')
      }
      if (!data) throw new Error('Could not unsubscribe just now. Try again in a moment.')
      return data
    },
  })

  return (
    <Card className="mx-auto max-w-md">
      <CardHeader>
        <CardTitle className="text-2xl">{unsubscribe.data ? 'You are unsubscribed' : 'Unsubscribe'}</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        {!token ? (
          <p className="text-sm text-muted-foreground">This link is incomplete. Open it again from the email.</p>
        ) : unsubscribe.data ? (
          <p className="text-sm text-muted-foreground">
            You will no longer get emails about {topics[unsubscribe.data.topic] ?? 'this'}. Receipts and account emails still arrive.
          </p>
        ) : (
          <>
            <p className="text-sm text-muted-foreground">Stop getting emails like the one you came from?</p>
            <Button onClick={() => unsubscribe.mutate()} disabled={unsubscribe.isPending}>
              Unsubscribe
            </Button>
            {unsubscribe.error && (
              <Alert variant="destructive" role="alert">
                <AlertDescription>{unsubscribe.error.message}</AlertDescription>
              </Alert>
            )}
          </>
        )}
        <p className="text-sm">
          <Link to="/me/account" className="underline underline-offset-4">
            Choose all your emails in your account
          </Link>
        </p>
      </CardContent>
    </Card>
  )
}
