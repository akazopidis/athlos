import { Menu, Search, ShoppingBag, X } from 'lucide-react'
import { useRef, type FormEvent } from 'react'
import { Link, NavLink, useLocation, useNavigate, useSearchParams } from 'react-router'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useCart } from '@/lib/cart'
import { Categories } from '@/lib/categories'
import { cn } from '@/lib/utils'
import { AccountMenu } from './AccountMenu.tsx'
import { Logo } from './Logo.tsx'
import { useDisclosure } from './useDisclosure.ts'

// The shop's categories (design §8), as filters of the catalog on "/".
const categories = [{ label: 'All programs', category: null as string | null }, ...Categories.map((c) => ({ label: c.label, category: c.value as string | null }))]
const categoryLink = (category: string | null) => (category ? `/?category=${category}` : '/')

// A shop header: logo, search, account and cart on top; the categories underneath from tablet width
// up (scrolling sideways when they do not fit), and in a panel under the header on phones. Sticky, so search and cart are always at hand.
export function SiteHeader() {
  const header = useRef<HTMLElement>(null)
  const menu = useDisclosure(header)
  const { pathname } = useLocation()
  const [params] = useSearchParams()
  const onHome = pathname === '/'
  const activeCategory = onHome ? params.get('category') : undefined

  return (
    <header ref={header} className="sticky top-0 z-40 border-b bg-background/90 backdrop-blur-lg">
      <div className="container-page flex h-16 items-center gap-3 sm:gap-6">
        <Link to="/" aria-label="Athlos home" className="shrink-0">
          <Logo />
        </Link>
        <HeaderSearch key={`${pathname}?${params.get('q') ?? ''}`} className="hidden flex-1 md:block" />
        <div className="ml-auto flex items-center gap-1 sm:gap-2">
          <Link to="/coaches" className="hidden px-2 text-sm font-medium text-muted-foreground transition-colors hover:text-foreground lg:block">
            Coaches
          </Link>
          <Link to="/studio/new" className="hidden px-2 text-sm font-medium text-muted-foreground transition-colors hover:text-foreground lg:block">
            Sell on Athlos
          </Link>
          <CartLink />
          <AccountMenu />
          <Button variant="ghost" size="icon" className="md:hidden" aria-label="Menu" aria-expanded={menu.open} aria-controls="mobile-menu" onClick={menu.toggle}>
            {menu.open ? <X className="size-5" aria-hidden /> : <Menu className="size-5" aria-hidden />}
          </Button>
        </div>
      </div>
      <div className="container-page pb-3 md:hidden">
        <HeaderSearch key={`m${pathname}?${params.get('q') ?? ''}`} />
      </div>
      <nav className="hidden border-t md:block" aria-label="Categories">
        <ul className="container-page flex h-11 items-center gap-1 overflow-x-auto text-sm font-medium [scrollbar-width:none]">
          {categories.map((c) => {
            const active = onHome && (activeCategory ?? null) === c.category
            return (
              <li key={c.label} className="shrink-0">
                <Link
                  to={categoryLink(c.category)}
                  aria-current={active ? 'page' : undefined}
                  className={cn('rounded-full px-3 py-1.5 whitespace-nowrap transition-colors hover:bg-muted hover:text-foreground', active ? 'bg-muted text-foreground' : 'text-muted-foreground')}
                >
                  {c.label}
                </Link>
              </li>
            )
          })}
        </ul>
      </nav>
      {menu.open && (
        <nav id="mobile-menu" className="container-page flex flex-col border-t pt-2 pb-4 md:hidden" aria-label="Main">
          {[
            ...categories.map((c) => ({ to: categoryLink(c.category), label: c.label })),
            { to: '/coaches', label: 'Coaches' },
            { to: '/cart', label: 'Cart' },
            { to: '/studio/new', label: 'Sell on Athlos' },
          ].map((l) => (
            <NavLink
              key={l.to}
              to={l.to}
              end
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

// Searches the catalog from any page: the text goes to "/?q=", keeping the category picked there.
function HeaderSearch({ className }: { className?: string }) {
  const navigate = useNavigate()
  const { pathname } = useLocation()
  const [params] = useSearchParams()
  const current = pathname === '/' ? (params.get('q') ?? '') : ''

  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const q = String(new FormData(e.currentTarget).get('q') ?? '').trim()
    const next = new URLSearchParams(pathname === '/' ? params : undefined)
    next.delete('page')
    if (q) next.set('q', q)
    else next.delete('q')
    navigate({ pathname: '/', search: next.toString() })
  }

  return (
    <form role="search" onSubmit={submit} className={cn('w-full max-w-xl', className)}>
      <div className="relative">
        <Search className="pointer-events-none absolute top-1/2 left-3.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
        <Input name="q" type="search" aria-label="Search programs" placeholder="Search programs: strength, running, mobility…" defaultValue={current} className="h-10 rounded-full bg-muted/60 pl-10" />
      </div>
    </form>
  )
}

function CartLink() {
  const { items } = useCart()
  const count = items.length
  return (
    <Button asChild variant="ghost" size="icon" className="relative">
      <Link to="/cart" aria-label={count ? `Cart, ${count} ${count === 1 ? 'program' : 'programs'}` : 'Cart'}>
        <ShoppingBag className="size-5" aria-hidden />
        {count > 0 && (
          <span aria-hidden className="absolute -top-0.5 -right-0.5 flex min-w-5 items-center justify-center rounded-full bg-volt px-1 text-[0.7rem] leading-5 font-bold text-volt-foreground tabular-nums">
            {count}
          </span>
        )}
      </Link>
    </Button>
  )
}
