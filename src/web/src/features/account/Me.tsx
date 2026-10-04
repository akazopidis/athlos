import { PageHeader } from '@/components/page'
import { useApp } from '@/lib/AppContext'
import { useTitle } from '@/lib/useTitle'
import { MyOrders } from './MyOrders.tsx'
import { MyPrograms } from './MyPrograms.tsx'
import { MyWorkspaces } from './MyWorkspaces.tsx'

// The customer area (design §8, "/me"): the programs you bought, your orders, and the workspaces you coach in.
export function Me() {
  const { msal } = useApp()
  useTitle('My account')
  if (!msal?.getActiveAccount()) return <p>Sign in to see your programs, orders and workspaces.</p>

  return (
    <div className="space-y-12">
      <PageHeader title="My account" />
      <MyPrograms />
      <MyOrders />
      <MyWorkspaces />
    </div>
  )
}
