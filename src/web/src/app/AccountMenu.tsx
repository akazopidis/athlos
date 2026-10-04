import { useQuery } from '@tanstack/react-query'
import { LogOut, UserRound } from 'lucide-react'
import { useRef } from 'react'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { useDisclosure } from './useDisclosure.ts'

export function AccountMenu() {
  const { msal, config, api } = useApp()
  const account = msal?.getActiveAccount()
  const container = useRef<HTMLDivElement>(null)
  const menu = useDisclosure(container)

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

  const name = account.name || account.username
  const initials = name
    .split(/[\s@.]+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]!.toUpperCase())
    .join('')

  return (
    <div ref={container} className="relative">
      <button
        type="button"
        aria-label="Account"
        aria-expanded={menu.open}
        aria-controls="account-menu"
        onClick={menu.toggle}
        className="flex size-10 items-center justify-center rounded-full outline-none focus-visible:ring-3 focus-visible:ring-ring/50"
      >
        <span className="flex size-9 items-center justify-center rounded-full bg-volt text-sm font-bold text-volt-foreground">{initials || '?'}</span>
      </button>
      {menu.open && (
        <div id="account-menu" className="absolute right-0 mt-2 w-64 rounded-2xl border bg-popover p-2 text-popover-foreground shadow-xl animate-in fade-in-0 zoom-in-95">
          <p className="truncate px-3 py-2 text-sm text-muted-foreground">{name}</p>
          <div className="my-1 border-t" />
          <Link to="/me" onClick={menu.close} className="flex items-center gap-2 rounded-lg px-3 py-2.5 text-sm font-medium hover:bg-muted">
            <UserRound className="size-4" aria-hidden /> My account
          </Link>
          <button
            type="button"
            onClick={() => msal.logoutRedirect()}
            className="flex w-full items-center gap-2 rounded-lg px-3 py-2.5 text-left text-sm font-medium hover:bg-muted"
          >
            <LogOut className="size-4" aria-hidden /> Sign out
          </button>
        </div>
      )}
    </div>
  )
}
