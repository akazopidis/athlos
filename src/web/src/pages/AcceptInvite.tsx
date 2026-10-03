import { useMutation } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { useApp } from '../AppContext.tsx'

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
  const { api, msal, config } = useApp()
  const [token] = useState(readToken)
  const signedIn = Boolean(msal?.getActiveAccount())

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

  if (!token) return <p>This invite link is incomplete. Open it again from the email.</p>

  if (!signedIn) {
    return (
      <section>
        <h1>Join a workspace on Athlos</h1>
        <p>Sign in, or sign up, with the email address the invite was sent to.</p>
        {msal && (
          <button type="button" onClick={() => msal.loginRedirect({ scopes: [config.apiScope!] })}>
            Sign in to accept
          </button>
        )}
      </section>
    )
  }

  if (accept.data) {
    return (
      <section>
        <h1>Welcome to {accept.data.displayName}</h1>
        <p>
          You're now staff. <Link to={`/studio/${accept.data.slug}`}>Open the studio</Link>
        </p>
      </section>
    )
  }

  return (
    <section>
      <h1>Join a workspace on Athlos</h1>
      <p>You've been invited to join this workspace as staff.</p>
      <button type="button" onClick={() => accept.mutate()} disabled={accept.isPending}>
        Accept invite
      </button>
      {accept.error && <p role="alert">{accept.error.message}</p>}
    </section>
  )
}
