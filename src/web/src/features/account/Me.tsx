import { Dumbbell, Receipt, Settings, Store } from 'lucide-react'
import { NavLink, Outlet } from 'react-router'
import { Notice, PageHeader } from '@/components/page'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { cn } from '@/lib/utils'

// The account pages (design §8, "/me"), one per subject, reached from the account menu or these tabs:
// the programs you bought ("/me", where Stripe returns you), your orders, the workspaces you coach in,
// and your account settings.
const AccountPages = [
  { to: '/me', label: 'My programs', icon: Dumbbell },
  { to: '/me/orders', label: 'Orders', icon: Receipt },
  { to: '/me/workspaces', label: 'Workspaces', icon: Store },
  { to: '/me/account', label: 'Account settings', icon: Settings },
] as const

export function Me() {
  const { msal, config } = useApp()
  const account = msal?.getActiveAccount()

  if (!account) {
    return (
      <Notice
        title="Sign in to see your programs"
        action={msal && <Button onClick={() => msal.loginRedirect({ scopes: [config.apiScope!] })}>Sign in</Button>}
      >
        Your programs, orders and workspaces are here once you sign in.
      </Notice>
    )
  }

  const firstName = account.name?.split(' ')[0]
  return (
    <div className="space-y-8">
      <PageHeader eyebrow="My account" title={firstName ? `Hi, ${firstName}` : 'Welcome back'} />
      <nav aria-label="My account" className="-mx-4 overflow-x-auto px-4 sm:mx-0 sm:px-0">
        <ul className="flex w-max gap-1 rounded-full border bg-muted/50 p-1 text-sm font-medium">
          {AccountPages.map((p) => (
            <li key={p.to}>
              <NavLink
                to={p.to}
                end
                className={({ isActive }) =>
                  cn(
                    'flex items-center gap-2 rounded-full px-4 py-2 whitespace-nowrap transition-colors',
                    isActive ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
                  )
                }
              >
                <p.icon className="size-4" aria-hidden /> {p.label}
              </NavLink>
            </li>
          ))}
        </ul>
      </nav>
      <Outlet />
    </div>
  )
}
