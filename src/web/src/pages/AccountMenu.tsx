import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { useApp } from '../AppContext.tsx'

export function AccountMenu() {
  const { msal, config, api } = useApp()
  const account = msal?.getActiveAccount()

  // Keeps user_profiles in step with the token after each sign-in (design §5.2).
  useQuery({
    queryKey: ['profile', account?.localAccountId],
    queryFn: async () => (await api.PUT('/api/v1/me/profile')).data ?? null,
    enabled: Boolean(account),
    staleTime: Infinity,
  })

  if (!msal) return null

  if (!account) {
    return (
      <button type="button" onClick={() => msal.loginRedirect({ scopes: [config.apiScope!] })}>
        Sign in
      </button>
    )
  }

  return (
    <nav>
      <Link to="/me">My account</Link>
      <span>{account.name ?? account.username}</span>
      <button type="button" onClick={() => msal.logoutRedirect()}>
        Sign out
      </button>
    </nav>
  )
}
