import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'

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
      <Button size="sm" onClick={() => msal.loginRedirect({ scopes: [config.apiScope!] })}>
        Sign in
      </Button>
    )
  }

  return (
    <nav className="flex items-center gap-3 text-sm">
      <Button asChild variant="ghost" size="sm">
        <Link to="/me">My account</Link>
      </Button>
      <span className="hidden text-muted-foreground md:inline">{account.name ?? account.username}</span>
      <Button variant="outline" size="sm" onClick={() => msal.logoutRedirect()}>
        Sign out
      </Button>
    </nav>
  )
}
