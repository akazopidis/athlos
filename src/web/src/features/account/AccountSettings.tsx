import { LogOut } from 'lucide-react'
import { Section } from '@/components/page'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { useTitle } from '@/lib/useTitle'
import { DeleteAccount } from './DeleteAccount.tsx'

// "/me/account": signing out, and deleting the account (design §5.2). Sign-in details (password,
// email) belong to Entra External ID, not to Athlos.
export function AccountSettings() {
  const { msal } = useApp()
  useTitle('Account settings')
  const name = msal?.getActiveAccount()?.name

  return (
    <div className="space-y-10">
      <Section title="Account settings">
        <div className="flex flex-wrap items-center justify-between gap-4 rounded-2xl border bg-card p-4">
          <div className="min-w-0">
            {name && <p className="truncate font-semibold">{name}</p>}
            <p className="text-sm text-muted-foreground">Your password and email are managed by your Athlos sign-in.</p>
          </div>
          <Button variant="outline" onClick={() => msal?.logoutRedirect()}>
            <LogOut aria-hidden /> Sign out
          </Button>
        </div>
      </Section>
      <DeleteAccount />
    </div>
  )
}
