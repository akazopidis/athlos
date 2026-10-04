import { ArrowRight, Check, CreditCard, Search, TrendingUp } from 'lucide-react'
import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { Section } from '@/components/page'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useTitle } from '@/lib/useTitle'
import { cn } from '@/lib/utils'
import { ProgramList } from './ProgramList.tsx'
import { Levels, useProgramSearch } from './useProgramSearch.ts'

// The marketplace (design §8, "/"): browse and search all coaches' published programs.
export function Home() {
  const { search, update } = useProgramSearch()
  useTitle('Programs')
  const [text, setText] = useState(search.q ?? '')

  // Search as the visitor types, once they pause.
  useEffect(() => {
    if (text === (search.q ?? '')) return
    const timer = setTimeout(() => update({ q: text }), 300)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- only the typed text starts a search
  }, [text])

  const submit = (e: FormEvent) => {
    e.preventDefault()
    update({ q: text })
    document.getElementById('programs')?.scrollIntoView({ behavior: 'smooth' })
  }

  return (
    <>
      <section className="relative isolate overflow-hidden bg-ink text-ink-foreground">
        <div
          aria-hidden
          className="absolute inset-0 -z-10"
          style={{
            background:
              'radial-gradient(60% 70% at 85% 0%, color-mix(in oklch, var(--volt) 35%, transparent), transparent 70%), radial-gradient(50% 60% at 0% 100%, color-mix(in oklch, var(--volt) 12%, transparent), transparent 70%)',
          }}
        />
        <div aria-hidden className="absolute inset-0 -z-10 opacity-[0.07] [background-image:repeating-linear-gradient(115deg,white_0_1px,transparent_1px_28px)]" />
        <div className="container-page py-16 sm:py-24 lg:py-28">
          <p className="text-xs font-semibold tracking-[0.25em] text-volt uppercase">Programs from independent coaches</p>
          <h1 className="mt-4 max-w-4xl text-5xl leading-[0.95] font-black tracking-tight uppercase sm:text-7xl lg:text-8xl" style={{ fontStretch: '78%' }}>
            Train with coaches you <span className="text-volt">trust.</span>
          </h1>
          <p className="mt-6 max-w-xl text-lg text-ink-foreground/75">
            Buy a program once and it's yours: every workout, at your own pace, with your progress saved as you go.
          </p>
          <form role="search" onSubmit={submit} className="mt-8 max-w-xl">
            <div className="relative">
              <Search className="pointer-events-none absolute top-1/2 left-5 size-5 -translate-y-1/2 text-ink-foreground/60" aria-hidden />
              <Input
                type="search"
                aria-label="Search programs"
                placeholder="Strength, running, mobility…"
                className="h-14 rounded-full border-white/15 bg-white/10 pr-5 pl-13 text-base text-ink-foreground placeholder:text-ink-foreground/50 focus-visible:border-volt focus-visible:ring-volt/30 md:text-base dark:bg-white/10"
                value={text}
                onChange={(e) => setText(e.target.value)}
              />
            </div>
          </form>
          <div className="mt-5 flex flex-wrap gap-2" role="group" aria-label="Level">
            {[undefined, ...Levels].map((level) => {
              const active = search.level === level
              return (
                <button
                  key={level ?? 'any'}
                  type="button"
                  aria-pressed={active}
                  onClick={() => update({ level: level ?? null })}
                  className={cn(
                    'h-9 rounded-full border px-4 text-sm font-medium transition-colors outline-none focus-visible:ring-3 focus-visible:ring-volt/40',
                    active ? 'border-volt bg-volt text-volt-foreground' : 'border-white/20 text-ink-foreground/80 hover:border-white/40 hover:text-ink-foreground',
                  )}
                >
                  {level ?? 'All levels'}
                </button>
              )
            })}
          </div>
        </div>
      </section>

      <div className="container-page space-y-20 py-12 sm:py-16">
        <Section title={search.q ? `Results for “${search.q}”` : search.level ? `${search.level} programs` : 'Latest programs'}>
          <div id="programs" className="scroll-mt-24">
            <ProgramList search={search} onPage={(page) => update({ page })} />
          </div>
        </Section>
        <HowItWorks />
        <CoachInvite />
      </div>
    </>
  )
}

const steps = [
  { icon: Search, title: 'Find your program', text: 'Browse programs from independent coaches and filter by level.' },
  { icon: CreditCard, title: 'Buy once, keep it', text: 'One secure payment through Stripe. No subscription: the program stays yours.' },
  { icon: TrendingUp, title: 'Train and track', text: 'Follow the workouts week by week and tick each one off as you go.' },
]

function HowItWorks() {
  return (
    <section aria-labelledby="how-it-works" className="space-y-8">
      <h2 id="how-it-works" className="text-2xl font-bold tracking-tight sm:text-3xl">
        How it works
      </h2>
      <ol className="grid gap-4 sm:grid-cols-3">
        {steps.map((s, i) => (
          <li key={s.title} className="rounded-3xl border bg-card p-6">
            <div className="flex items-center justify-between">
              <span className="flex size-11 items-center justify-center rounded-2xl bg-volt text-volt-foreground">
                <s.icon className="size-5" aria-hidden />
              </span>
              <span className="font-heading text-4xl font-black text-muted-foreground/30">{i + 1}</span>
            </div>
            <h3 className="mt-5 text-lg font-bold">{s.title}</h3>
            <p className="mt-1 text-sm text-muted-foreground">{s.text}</p>
          </li>
        ))}
      </ol>
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
            <Link to="/studio/new">
              Open your studio <ArrowRight aria-hidden />
            </Link>
          </Button>
        </div>
      </div>
    </section>
  )
}
