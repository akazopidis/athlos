import type { AccountInfo } from '@azure/msal-browser'
import { signInConfigured, type AppConfig } from './config.ts'

export type Account = Pick<AccountInfo, 'username' | 'localAccountId' | 'name'>

// Sign-in as the pages see it (design §5). Who is signed in is known before the first render.
export interface Auth {
  readonly account: Account | null
  // Entra's sign-in page; back on redirectStartPage (default: the current page) afterwards.
  signIn(redirectStartPage?: string): Promise<void>
  signOut(): Promise<void>
  // An access token for the Athlos API, or null when nobody is signed in.
  token(): Promise<string | null>
}

// MSAL is 63 kB compressed and most visitors never sign in, so it loads only when it has work: the
// page is Entra's redirect back, this tab signed in or started signing in (MSAL keeps its cache in
// sessionStorage, per tab), or the visitor asks to sign in (docs/evaluation/lighthouse.md).
const SignedInKey = 'athlos.signedIn'

export async function createAuth(config: AppConfig): Promise<Auth | null> {
  if (!signInConfigured(config)) return null

  let loading: ReturnType<typeof loadMsal> | null = null
  const load = () => (loading ??= loadMsal(config))
  const msal = mayBeSignedIn() ? await load() : null

  const active = msal?.instance.getActiveAccount() ?? null
  remember(Boolean(active))
  const account = active && { username: active.username, localAccountId: active.localAccountId, name: active.name }

  return {
    account,
    signIn: async (redirectStartPage) => {
      // Set before leaving for Entra: MSAL may finish on redirectStartPage, without the fragment. If no
      // account comes back (cancelled, abandoned), the next load clears it.
      remember(true)
      const { instance } = await load()
      await instance.loginRedirect({ scopes: [config.apiScope], redirectStartPage })
    },
    signOut: async () => {
      remember(false)
      const { instance } = await load()
      await instance.logoutRedirect()
    },
    token: async () => (msal ? msal.getAccessToken(msal.instance, config.apiScope) : null),
  }
}

async function loadMsal(config: Parameters<typeof import('./msal.ts').createMsal>[0]) {
  const { createMsal, getAccessToken } = await import('./msal.ts')
  return { instance: await createMsal(config), getAccessToken }
}

export function mayBeSignedIn(): boolean {
  // Entra answers in the fragment (MSAL's default response mode): #code=…&state=… or #error=…&state=…
  const answer = new URLSearchParams(window.location.hash.slice(1))
  if (answer.has('state') && (answer.has('code') || answer.has('error'))) return true
  try {
    if (sessionStorage.getItem(SignedInKey)) return true
    // Tabs signed in before this marker existed: MSAL's own list of accounts (msal.<schema>.account.keys).
    return Object.keys(sessionStorage).some((key) => key.startsWith('msal.') && key.endsWith('.account.keys') && sessionStorage.getItem(key) !== '[]')
  } catch {
    return false
  }
}

function remember(signedIn: boolean) {
  try {
    if (signedIn) sessionStorage.setItem(SignedInKey, '1')
    else sessionStorage.removeItem(SignedInKey)
  } catch {
    // Storage blocked: MSAL cannot keep a session in this tab either.
  }
}
