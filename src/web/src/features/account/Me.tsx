import { Notice, PageHeader } from '@/components/page'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { useTitle } from '@/lib/useTitle'
import { MyOrders } from './MyOrders.tsx'
import { MyPrograms } from './MyPrograms.tsx'
import { MyWorkspaces } from './MyWorkspaces.tsx'

// The customer area (design §8, "/me"): the programs you bought, your orders, and the workspaces you coach in.
export function Me() {
  const { msal, config } = useApp()
  useTitle('My account')
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
    <div className="space-y-14">
      <PageHeader eyebrow="My account" title={firstName ? `Hi, ${firstName}` : 'Welcome back'} description="Pick up where you left off." />
      <MyPrograms />
      <MyOrders />
      <MyWorkspaces />
    </div>
  )
}
