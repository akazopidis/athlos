import { useMutation } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { useApp } from '@/lib/AppContext'
import { useTitle } from '@/lib/useTitle'

const TokenKey = 'athlos.invite-token'

// The link in a staff invite email: /invites/{workspace}#{token} (design §5.2). The token is in the
// fragment, which the browser never sends to a server; this page sends it in a request body instead.
// It is kept in sessionStorage across the sign-in redirect, and removed once used.
function readToken(): string | null {
  const fromLink = window.location.hash.slice(1)
  try {
    if (fromLink) {
      sessionStorage.setItem(TokenKey, fromLink)
      window.history.replaceState(null, '', window.location.pathname)
      return fromLink
    }
    return sessionStorage.getItem(TokenKey)
  } catch {
    return fromLink || null
  }
}

export function AcceptInvite() {
  const { slug = '' } = useParams()
  const { api, auth } = useApp()
  const [token] = useState(readToken)
  const signedIn = Boolean(auth?.account)
  useTitle('Join a workspace')

  const accept = useMutation({
    mutationFn: async () => {
      const { data, response } = await api.POST('/api/v1/invites/{tenantSlug}/accept', {
        params: { path: { tenantSlug: slug } },
        body: { token: token ?? '' },
      })
      if (response.status === 404) {
        throw new Error('This invite is not valid for your account. It may have expired, been revoked or used, or be meant for another email address.')
      }
      if (!data) throw new Error('Could not accept the invite.')
      try {
        sessionStorage.removeItem(TokenKey)
      } catch {
        // Nothing stored.
      }
      return data
    },
  })

  return (
    <Card className="mx-auto max-w-md">
      <CardHeader>
        <CardTitle className="text-2xl">{accept.data ? `Welcome to ${accept.data.displayName}` : 'Join a workspace on Athlos'}</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        {!token ? (
          <p className="text-sm text-muted-foreground">This invite link is incomplete. Open it again from the email.</p>
        ) : !signedIn ? (
          <>
            <p className="text-sm text-muted-foreground">Sign in, or sign up, with the email address the invite was sent to.</p>
            {auth && <Button onClick={() => auth.signIn()}>Sign in to accept</Button>}
          </>
        ) : accept.data ? (
          <>
            <p className="text-sm text-muted-foreground">You're now staff.</p>
            <Button asChild>
              <Link to={`/studio/${accept.data.slug}`}>Open the studio</Link>
            </Button>
          </>
        ) : (
          <>
            <p className="text-sm text-muted-foreground">You've been invited to join this workspace as staff.</p>
            <Button onClick={() => accept.mutate()} disabled={accept.isPending}>
              Accept invite
            </Button>
            {accept.error && (
              <Alert variant="destructive">
                <AlertDescription>{accept.error.message}</AlertDescription>
              </Alert>
            )}
          </>
        )}
      </CardContent>
    </Card>
  )
}
