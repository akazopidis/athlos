import { LogOut } from 'lucide-react'
import { Section } from '@/components/page'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { useTitle } from '@/lib/useTitle'
import { DeleteAccount } from './DeleteAccount.tsx'
import { DownloadMyData } from './DownloadMyData.tsx'
import { EmailPreferences } from './EmailPreferences.tsx'

// "/me/account": signing out, the optional emails (design §5.3), downloading your data (design §5.4), and deleting the account (§5.2). Sign-in details (password,
// email) belong to Entra External ID, not to Athlos.
export function AccountSettings() {
  const { auth } = useApp()
  useTitle('Account settings')
  const name = auth?.account?.name

  return (
    <div className="space-y-10">
      <Section title="Account settings">
        <div className="flex flex-wrap items-center justify-between gap-4 rounded-2xl border bg-card p-4">
          <div className="min-w-0">
            {name && <p className="truncate font-semibold">{name}</p>}
            <p className="text-sm text-muted-foreground">Your password and email are managed by your Athlos sign-in.</p>
          </div>
          <Button variant="outline" onClick={() => auth?.signOut()}>
            <LogOut aria-hidden /> Sign out
          </Button>
        </div>
        <EmailPreferences />
        <DownloadMyData />
      </Section>
      <DeleteAccount />
    </div>
  )
}
