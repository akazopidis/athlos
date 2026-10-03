import { Dumbbell } from 'lucide-react'
import { Link, NavLink, Route, Routes } from 'react-router'
import { cn } from '@/lib/utils'
import { Footer } from './Footer.tsx'
import { AcceptInvite } from './pages/AcceptInvite.tsx'
import { AccountMenu } from './pages/AccountMenu.tsx'
import { CreateWorkspace } from './pages/CreateWorkspace.tsx'
import { Home } from './pages/Home.tsx'
import { Me } from './pages/Me.tsx'
import { MyProgramPage } from './pages/MyProgramPage.tsx'
import { ProgramPage } from './pages/ProgramPage.tsx'
import { Storefront } from './pages/Storefront.tsx'
import { Studio } from './pages/Studio.tsx'
import { StudioOverview } from './pages/studio/StudioOverview.tsx'
import { StudioProgramEditor } from './pages/studio/StudioProgramEditor.tsx'
import { StudioSales } from './pages/studio/StudioSales.tsx'
import { StudioSettings } from './pages/studio/StudioSettings.tsx'

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
      </main>
      <Footer />
    </div>
  )
}

function navLink({ isActive }: { isActive: boolean }) {
  return cn('text-muted-foreground transition-colors hover:text-foreground', isActive && 'text-foreground')
}
