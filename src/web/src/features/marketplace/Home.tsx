import { ArrowRight, Check, CreditCard, TrendingUp, Wallet } from 'lucide-react'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { categoryLabel } from '@/lib/categories'
import { useTitle } from '@/lib/useTitle'
import { Catalog } from './Catalog.tsx'
import { CoachGrid } from './CoachGrid.tsx'
import { useMarketplaceCoaches } from './useMarketplaceCoaches.ts'
import { useProgramSearch } from './useProgramSearch.ts'

// The marketplace (design §8, "/"), laid out as a shop: a short banner, the shop's promises, then the
// catalog of every coach's published programs. The search box and the categories are in the
// header, so they work from every page.
export function Home() {
  const { search, update, clear } = useProgramSearch()
  useTitle(search.q ? `“${search.q}”` : 'Programs')
  const heading = search.q ? `Results for “${search.q}”` : search.category ? `${categoryLabel(search.category)} programs` : 'All programs'

  return (
    <>
      {!search.q && (
        <section className="relative isolate overflow-hidden bg-ink text-ink-foreground">
          <div
            aria-hidden
            className="absolute inset-0 -z-10"
            style={{
              background:
                'radial-gradient(60% 90% at 90% 0%, color-mix(in oklch, var(--volt) 35%, transparent), transparent 70%), radial-gradient(40% 70% at 0% 100%, color-mix(in oklch, var(--volt) 12%, transparent), transparent 70%)',
            }}
          />
          <div aria-hidden className="absolute inset-0 -z-10 opacity-[0.07] [background-image:repeating-linear-gradient(115deg,white_0_1px,transparent_1px_28px)]" />
          <div className="container-page flex flex-wrap items-end justify-between gap-6 py-10 sm:py-14">
            <div className="max-w-2xl">
              <p className="text-xs font-semibold tracking-[0.25em] text-volt uppercase">Programs from independent coaches</p>
              <h1 className="mt-3 text-4xl leading-[0.95] font-black tracking-tight uppercase sm:text-6xl" style={{ fontStretch: '78%' }}>
                Train with coaches you <span className="text-volt">trust.</span>
              </h1>
              <p className="mt-4 max-w-xl text-ink-foreground/75">Pick programs from any coach, pay once at checkout, and keep every workout.</p>
            </div>
            <Button asChild size="lg" className="bg-volt text-volt-foreground hover:bg-volt/90">
              <a href="#programs">
                Shop programs <ArrowRight aria-hidden />
              </a>
            </Button>
          </div>
        </section>
      )}

      <Promises />

      <div className="container-page space-y-16 py-10 sm:py-12">
        <section id="programs" aria-labelledby="catalog-heading" className="scroll-mt-36 space-y-6">
          <h2 id="catalog-heading" className="text-2xl font-bold tracking-tight sm:text-3xl">
            {heading}
          </h2>
          <Catalog search={search} update={update} clear={clear} />
        </section>
        <FeaturedCoaches />
        <CoachInvite />
      </div>
    </>
  )
}

const promises = [
  { icon: Wallet, title: 'Pay once', text: 'No subscription' },
  { icon: CreditCard, title: 'One secure checkout', text: 'Every coach in one payment, by Stripe' },
  { icon: TrendingUp, title: 'Track your progress', text: 'Tick off workouts week by week' },
]

// The shop's promises, in a strip under the banner.
function Promises() {
  return (
    <div className="border-b bg-muted/40">
      <ul className="container-page grid gap-3 py-4 text-sm sm:grid-cols-3">
        {promises.map((p) => (
          <li key={p.title} className="flex items-center gap-3">
            <span className="flex size-9 shrink-0 items-center justify-center rounded-xl bg-volt text-volt-foreground">
              <p.icon className="size-4" aria-hidden />
            </span>
            <span>
              <span className="block font-semibold">{p.title}</span>
              <span className="text-muted-foreground">{p.text}</span>
            </span>
          </li>
        ))}
      </ul>
    </div>
  )
}

// The coaches with the most programs on sale; hidden until there are some.
function FeaturedCoaches() {
  const { coaches } = useMarketplaceCoaches(4)
  if (coaches.length === 0) return null
  return (
    <section aria-labelledby="coaches-heading" className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <h2 id="coaches-heading" className="text-2xl font-bold tracking-tight sm:text-3xl">
          Featured coaches
        </h2>
        <Link to="/coaches" className="inline-flex items-center gap-1 text-sm font-medium text-muted-foreground hover:text-foreground">
          All coaches <ArrowRight className="size-4" aria-hidden />
        </Link>
      </div>
      <CoachGrid coaches={coaches} />
    </section>
  )
}

function CoachInvite() {
  return (
    <section className="relative isolate overflow-hidden rounded-3xl bg-volt px-6 py-10 text-volt-foreground sm:px-12 sm:py-14">
      <div aria-hidden className="absolute -right-16 -bottom-24 -z-10 size-80 rounded-full border-[40px] border-volt-foreground/10" />
      <div className="grid gap-8 lg:grid-cols-[1.4fr_1fr] lg:items-center">
        <div className="space-y-4">
          <p className="text-xs font-semibold tracking-[0.25em] uppercase opacity-70">For coaches</p>
          <h2 className="text-3xl leading-tight font-black tracking-tight uppercase sm:text-5xl" style={{ fontStretch: '80%' }}>
            Sell your programs on Athlos
          </h2>
          <ul className="space-y-2">
            {['Your own storefront, in your colors', 'Payments straight to your Stripe account', 'Invite your team to help'].map((item) => (
              <li key={item} className="flex items-center gap-2 font-medium">
                <Check className="size-5 shrink-0" aria-hidden /> {item}
              </li>
            ))}
          </ul>
        </div>
        <div className="lg:justify-self-end">
          <Button asChild size="lg" className="bg-ink text-ink-foreground hover:bg-ink/85">
            <Link to="/sell">
              Start selling <ArrowRight aria-hidden />
            </Link>
          </Button>
        </div>
      </div>
    </section>
  )
}
