import { useApp } from '../AppContext.tsx'
import { MyOrders } from './MyOrders.tsx'
import { MyPrograms } from './MyPrograms.tsx'
import { MyWorkspaces } from './MyWorkspaces.tsx'

// The customer area (design §8, "/me"): the programs you bought, your orders, and the workspaces you coach in.
export function Me() {
  const { msal } = useApp()
  if (!msal?.getActiveAccount()) return <p>Sign in to see your programs, orders and workspaces.</p>

  return (
    <>
      <MyPrograms />
      <MyOrders />
      <MyWorkspaces />
    </>
  )
}
