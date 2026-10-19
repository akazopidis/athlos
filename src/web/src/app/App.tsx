import { lazy, Suspense, useEffect, type ComponentType } from 'react'
import { Link, Outlet, Route, Routes, useLocation } from 'react-router'
import { Loading, Notice } from '@/components/page'
import { Button } from '@/components/ui/button'
import { Home } from '@/features/marketplace/Home'
import { ProgramPage } from '@/features/marketplace/ProgramPage'
import { Storefront } from '@/features/marketplace/Storefront'
import { Footer } from './Footer.tsx'
import { SiteHeader } from './SiteHeader.tsx'

// The public pages a visitor lands on are in the main bundle; the signed-in areas (studio, account,
// invites) load when first opened, so a marketplace visitor does not download the studio.
const lazyPage = <K extends string>(load: () => Promise<Record<K, ComponentType>>, name: K) =>
  lazy(async () => ({ default: (await load())[name] }))
const Admin = lazyPage(() => import('@/features/admin/Admin'), 'Admin')
const Cart = lazyPage(() => import('@/features/cart/Cart'), 'Cart')
const Coaches = lazyPage(() => import('@/features/marketplace/Coaches'), 'Coaches')
const Sell = lazyPage(() => import('@/features/marketplace/Sell'), 'Sell')
const AcceptInvite = lazyPage(() => import('@/features/studio/AcceptInvite'), 'AcceptInvite')
const CreateWorkspace = lazyPage(() => import('@/features/studio/CreateWorkspace'), 'CreateWorkspace')
const Me = lazyPage(() => import('@/features/account/Me'), 'Me')
const MyPrograms = lazyPage(() => import('@/features/account/MyPrograms'), 'MyPrograms')
const MyOrders = lazyPage(() => import('@/features/account/MyOrders'), 'MyOrders')
const SavedPrograms = lazyPage(() => import('@/features/account/SavedPrograms'), 'SavedPrograms')
const MyWorkspaces = lazyPage(() => import('@/features/account/MyWorkspaces'), 'MyWorkspaces')
const AccountSettings = lazyPage(() => import('@/features/account/AccountSettings'), 'AccountSettings')
const MyProgramPage = lazyPage(() => import('@/features/account/MyProgramPage'), 'MyProgramPage')
const Studio = lazyPage(() => import('@/features/studio/Studio'), 'Studio')
const StudioOverview = lazyPage(() => import('@/features/studio/StudioOverview'), 'StudioOverview')
const StudioProgramEditor = lazyPage(() => import('@/features/studio/StudioProgramEditor'), 'StudioProgramEditor')
const StudioSales = lazyPage(() => import('@/features/studio/StudioSales'), 'StudioSales')
const StudioReviews = lazyPage(() => import('@/features/studio/StudioReviews'), 'StudioReviews')
const StudioSettings = lazyPage(() => import('@/features/studio/StudioSettings'), 'StudioSettings')

// Four areas (design §8): marketplace (with the cart), coach storefront, coach studio, customer area.
// The marketplace and storefronts run edge to edge (their banners); every other page sits in the
// page container.
export default function App() {
  const { pathname } = useLocation()
  // A new page starts at the top (the router keeps the scroll position otherwise).
  useEffect(() => {
    window.scrollTo(0, 0)
  }, [pathname])

  return (
    <div className="flex min-h-dvh flex-col bg-background text-foreground">
      <SiteHeader />
      <main className="flex-1">
        <Suspense fallback={<div className="container-page py-12"><Loading lines={4} /></div>}>
          <Routes>
            <Route path="/" element={<Home />} />
            <Route path="/c/:slug" element={<Storefront />} />
            <Route path="/sell" element={<Sell />} />
            <Route element={<Contained />}>
              <Route path="/c/:slug/programs/:programSlug" element={<ProgramPage />} />
              <Route path="/invites/:slug" element={<AcceptInvite />} />
              <Route path="/studio/new" element={<CreateWorkspace />} />
              <Route path="/studio/:slug" element={<Studio />}>
                <Route index element={<StudioOverview />} />
                <Route path="programs/:programId" element={<StudioProgramEditor />} />
                <Route path="sales" element={<StudioSales />} />
                <Route path="reviews" element={<StudioReviews />} />
                <Route path="settings" element={<StudioSettings />} />
              </Route>
              <Route path="/cart" element={<Cart />} />
              <Route path="/coaches" element={<Coaches />} />
              <Route path="/me" element={<Me />}>
                <Route index element={<MyPrograms />} />
                <Route path="saved" element={<SavedPrograms />} />
                <Route path="orders" element={<MyOrders />} />
                <Route path="workspaces" element={<MyWorkspaces />} />
                <Route path="account" element={<AccountSettings />} />
              </Route>
              <Route path="/me/programs/:programId" element={<MyProgramPage />} />
              <Route path="/admin" element={<Admin />} />
              <Route
                path="*"
                element={
                  <Notice
                    title="Page not found"
                    action={
                      <Button asChild>
                        <Link to="/">Browse programs</Link>
                      </Button>
                    }
                  >
                    The link may be old, or the page has moved.
                  </Notice>
                }
              />
            </Route>
          </Routes>
        </Suspense>
      </main>
      <Footer />
    </div>
  )
}

function Contained() {
  return (
    <div className="container-page py-8 sm:py-12">
      <Outlet />
    </div>
  )
}
