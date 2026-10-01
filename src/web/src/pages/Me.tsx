import { useApp } from '../AppContext.tsx'
import { MyOrders } from './MyOrders.tsx'
import { MyWorkspaces } from './MyWorkspaces.tsx'

// The customer area (design §8, "/me"): what you bought, and the workspaces you coach in.
export function Me() {
  const { msal } = useApp()
  if (!msal?.getActiveAccount()) return <p>Sign in to see your orders and workspaces.</p>

  return (
    <>
      <MyOrders />
      <MyWorkspaces />
    </>
  )
}
