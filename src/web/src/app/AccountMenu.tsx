import { useQuery } from '@tanstack/react-query'
import { Dumbbell, Heart, LogOut, Plus, Receipt, Settings, ShieldCheck, Store, type LucideIcon } from 'lucide-react'
import { useRef, type ReactNode } from 'react'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { usePlatformAdmin } from '@/lib/usePlatformAdmin'
import { useDisclosure } from './useDisclosure.ts'

// The signed-in user's menu: every account page directly (design §8, "/me/..."), the studio of each
// workspace they coach in, the platform admin for admins, and signing out.
export function AccountMenu() {
  const { msal, config, api } = useApp()
  const account = msal?.getActiveAccount()
  const container = useRef<HTMLDivElement>(null)
  const menu = useDisclosure(container)
  const isAdmin = usePlatformAdmin()

  // Keeps user_profiles in step with the token after each sign-in (design §5.2).
  useQuery({
    queryKey: ['profile', account?.localAccountId],
    queryFn: async () => (await api.PUT('/api/v1/me/profile')).data ?? null,
    enabled: Boolean(account),
    staleTime: Infinity,
  })

  const memberships = useQuery({
    queryKey: ['memberships'],
    queryFn: async () => (await api.GET('/api/v1/me/memberships')).data ?? [],
    enabled: Boolean(account),
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
  const studios = (memberships.data ?? []).filter((m) => m.status !== 'Suspended')

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
        <div
          id="account-menu"
          className="absolute right-0 mt-2 max-h-[calc(100dvh-5rem)] w-72 max-w-[calc(100vw-2rem)] overflow-y-auto rounded-2xl border bg-popover p-2 text-popover-foreground shadow-xl animate-in fade-in-0 zoom-in-95"
        >
          <p className="truncate px-3 pt-2 pb-1 text-sm font-semibold">{name}</p>
          <MenuLink to="/me" icon={Dumbbell} onClick={menu.close}>
            My programs
          </MenuLink>
          <MenuLink to="/me/saved" icon={Heart} onClick={menu.close}>
            Saved
          </MenuLink>
          <MenuLink to="/me/orders" icon={Receipt} onClick={menu.close}>
            Orders
          </MenuLink>
          <MenuLink to="/me/account" icon={Settings} onClick={menu.close}>
            Account settings
          </MenuLink>

          <Divider />
          <p className="px-3 pt-1 pb-1 text-xs font-semibold tracking-[0.15em] text-muted-foreground uppercase">Workspaces</p>
          {studios.map((m) => (
            <MenuLink key={m.slug} to={`/studio/${m.slug}`} icon={Store} onClick={menu.close}>
              <span className="truncate">{m.displayName}</span>
              <span className="ml-auto text-xs text-muted-foreground">{m.role}</span>
            </MenuLink>
          ))}
          <MenuLink to="/me/workspaces" icon={studios.length ? Store : Plus} onClick={menu.close}>
            {studios.length ? 'All workspaces' : 'Open a workspace'}
          </MenuLink>

          {isAdmin && (
            <>
              <Divider />
              <MenuLink to="/admin" icon={ShieldCheck} onClick={menu.close}>
                Platform admin
              </MenuLink>
            </>
          )}

          <Divider />
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

function MenuLink({ to, icon: Icon, onClick, children }: { to: string; icon: LucideIcon; onClick: () => void; children: ReactNode }) {
  return (
    <Link to={to} onClick={onClick} className="flex min-w-0 items-center gap-2 rounded-lg px-3 py-2.5 text-sm font-medium hover:bg-muted">
      <Icon className="size-4 shrink-0" aria-hidden /> {children}
    </Link>
  )
}

function Divider() {
  return <div className="my-1 border-t" />
}
