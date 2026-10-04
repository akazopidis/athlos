import { Dumbbell } from 'lucide-react'
import { lazy, Suspense, type ComponentType } from 'react'
import { Link, NavLink, Route, Routes } from 'react-router'
import { cn } from '@/lib/utils'
import { Footer } from './Footer.tsx'
import { Loading } from '@/components/page'
import { AccountMenu } from './AccountMenu.tsx'
import { Home } from '@/features/marketplace/Home'
import { ProgramPage } from '@/features/marketplace/ProgramPage'
import { Storefront } from '@/features/marketplace/Storefront'

// The public pages a visitor lands on are in the main bundle; the signed-in areas (studio, account,
// invites) load when first opened, so a marketplace visitor does not download the studio.
const lazyPage = <K extends string>(load: () => Promise<Record<K, ComponentType>>, name: K) =>
  lazy(async () => ({ default: (await load())[name] }))
const AcceptInvite = lazyPage(() => import('@/features/studio/AcceptInvite'), 'AcceptInvite')
const CreateWorkspace = lazyPage(() => import('@/features/studio/CreateWorkspace'), 'CreateWorkspace')
const Me = lazyPage(() => import('@/features/account/Me'), 'Me')
const MyProgramPage = lazyPage(() => import('@/features/account/MyProgramPage'), 'MyProgramPage')
const Studio = lazyPage(() => import('@/features/studio/Studio'), 'Studio')
const StudioOverview = lazyPage(() => import('@/features/studio/StudioOverview'), 'StudioOverview')
const StudioProgramEditor = lazyPage(() => import('@/features/studio/StudioProgramEditor'), 'StudioProgramEditor')
const StudioSales = lazyPage(() => import('@/features/studio/StudioSales'), 'StudioSales')
const StudioSettings = lazyPage(() => import('@/features/studio/StudioSettings'), 'StudioSettings')

// Four areas (design §8): marketplace, coach storefront, coach studio, customer area.
export default function App() {
  return (
    <div className="flex min-h-screen flex-col bg-background text-foreground">
      <header className="sticky top-0 z-10 border-b bg-background/90 backdrop-blur">
        <div className="mx-auto flex h-14 max-w-5xl items-center gap-6 px-4">
          <Link to="/" className="flex items-center gap-2 font-semibold tracking-tight">
            <Dumbbell className="size-5" aria-hidden />
            Athlos
          </Link>
          <nav className="hidden gap-4 text-sm sm:flex">
            <NavLink to="/" end className={navLink}>
              Programs
            </NavLink>
            <NavLink to="/studio/new" className={navLink}>
              For coaches
            </NavLink>
          </nav>
          <div className="ml-auto">
            <AccountMenu />
          </div>
        </div>
      </header>
      <main className="mx-auto w-full max-w-5xl flex-1 px-4 py-8">
        <Suspense fallback={<Loading lines={4} />}>
          <Routes>
          <Route path="/" element={<Home />} />
          <Route path="/c/:slug" element={<Storefront />} />
          <Route path="/c/:slug/programs/:programSlug" element={<ProgramPage />} />
          <Route path="/invites/:slug" element={<AcceptInvite />} />
          <Route path="/studio/new" element={<CreateWorkspace />} />
          <Route path="/studio/:slug" element={<Studio />}>
            <Route index element={<StudioOverview />} />
            <Route path="programs/:programId" element={<StudioProgramEditor />} />
            <Route path="sales" element={<StudioSales />} />
            <Route path="settings" element={<StudioSettings />} />
          </Route>
          <Route path="/me" element={<Me />} />
          <Route path="/me/programs/:programId" element={<MyProgramPage />} />
          </Routes>
        </Suspense>
      </main>
      <Footer />
    </div>
  )
}

function navLink({ isActive }: { isActive: boolean }) {
  return cn('text-muted-foreground transition-colors hover:text-foreground', isActive && 'text-foreground')
}
