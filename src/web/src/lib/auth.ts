import { InteractionRequiredAuthError, PublicClientApplication } from '@azure/msal-browser'
import type { AppConfig } from './config.ts'
import { signInConfigured } from './config.ts'

// Entra External ID with auth code + PKCE (design §5). Sign-up, email verification and password
// reset all happen on Entra's pages; Athlos never sees a password.
export async function createMsal(config: AppConfig): Promise<PublicClientApplication | null> {
  if (!signInConfigured(config)) return null

  const msal = new PublicClientApplication({
    auth: {
      clientId: config.entraClientId,
      authority: config.entraAuthority,
      knownAuthorities: [new URL(config.entraAuthority).host],
      redirectUri: window.location.origin,
      postLogoutRedirectUri: window.location.origin,
    },
    cache: { cacheLocation: 'sessionStorage' },
  })
  await msal.initialize()

  const result = await msal.handleRedirectPromise()
  const account = result?.account ?? msal.getAllAccounts()[0]
  if (account) msal.setActiveAccount(account)
  return msal
}

// An access token for the Athlos API, or null when nobody is signed in.
export async function getAccessToken(msal: PublicClientApplication | null, scope: string | null): Promise<string | null> {
  const account = msal?.getActiveAccount()
  if (!msal || !account || !scope) return null

  try {
    return (await msal.acquireTokenSilent({ account, scopes: [scope] })).accessToken
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      await msal.acquireTokenRedirect({ account, scopes: [scope] })
    }
    throw error
  }
}
