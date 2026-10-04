import { Menu, X } from 'lucide-react'
import { useRef } from 'react'
import { Link, NavLink } from 'react-router'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { AccountMenu } from './AccountMenu.tsx'
import { Logo } from './Logo.tsx'
import { useDisclosure } from './useDisclosure.ts'

const links = [
  { to: '/', label: 'Programs', end: true },
  { to: '/studio/new', label: 'For coaches', end: false },
]

// Sticky header: the links inline from tablet width up, in a panel under the header on phones.
export function SiteHeader() {
  const header = useRef<HTMLElement>(null)
  const menu = useDisclosure(header)

  return (
    <header ref={header} className="sticky top-0 z-40 border-b bg-background/85 backdrop-blur-lg">
      <div className="container-page flex h-16 items-center gap-8">
        <Link to="/" aria-label="Athlos home">
          <Logo />
        </Link>
        <nav className="hidden items-center gap-6 text-sm font-medium md:flex" aria-label="Main">
          {links.map((l) => (
            <NavLink key={l.to} to={l.to} end={l.end} className={({ isActive }) => cn('transition-colors hover:text-foreground', isActive ? 'text-foreground' : 'text-muted-foreground')}>
              {l.label}
            </NavLink>
          ))}
        </nav>
        <div className="ml-auto flex items-center gap-2">
          <AccountMenu />
          <Button variant="ghost" size="icon" className="md:hidden" aria-label="Menu" aria-expanded={menu.open} aria-controls="mobile-menu" onClick={menu.toggle}>
            {menu.open ? <X className="size-5" aria-hidden /> : <Menu className="size-5" aria-hidden />}
          </Button>
        </div>
      </div>
      {menu.open && (
        <nav id="mobile-menu" className="container-page flex flex-col border-t pt-2 pb-4 md:hidden" aria-label="Main">
          {[...links, { to: '/me', label: 'My account', end: false }].map((l) => (
            <NavLink
              key={l.to}
              to={l.to}
              end={l.end}
              onClick={menu.close}
              className={({ isActive }) => cn('rounded-lg px-3 py-3 text-base font-medium hover:bg-muted', isActive ? 'text-foreground' : 'text-muted-foreground')}
            >
              {l.label}
            </NavLink>
          ))}
        </nav>
      )}
    </header>
  )
}
