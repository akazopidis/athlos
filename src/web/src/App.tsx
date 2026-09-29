import { Link, Route, Routes } from 'react-router'
import { Footer } from './Footer.tsx'
import { AccountMenu } from './pages/AccountMenu.tsx'
import { CreateWorkspace } from './pages/CreateWorkspace.tsx'
import { Home } from './pages/Home.tsx'
import { MyWorkspaces } from './pages/MyWorkspaces.tsx'
import { Storefront } from './pages/Storefront.tsx'
import { Studio } from './pages/Studio.tsx'

// Four areas (design §8): marketplace, coach storefront, coach studio, customer area.
// Phase 2 has the parts identity and tenancy need; phase 6 builds the full UI.
export default function App() {
  return (
    <div className="page">
      <header>
        <Link to="/" className="brand">
          Athlos
        </Link>
        <AccountMenu />
      </header>
      <main>
        <Routes>
          <Route path="/" element={<Home />} />
          <Route path="/c/:slug" element={<Storefront />} />
          <Route path="/studio/new" element={<CreateWorkspace />} />
          <Route path="/studio/:slug" element={<Studio />} />
          <Route path="/me" element={<MyWorkspaces />} />
        </Routes>
      </main>
      <Footer />
    </div>
  )
}
