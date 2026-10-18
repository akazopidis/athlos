import { useQuery } from '@tanstack/react-query'
import { ArrowRight, BadgeCheck, Check, CreditCard, Eye, LineChart, Mail, Palette, RotateCcw, Rocket, Share2, Store, Users, Wallet } from 'lucide-react'
import type { ReactNode } from 'react'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { useTitle } from '@/lib/useTitle'

// "Sell on Athlos" (design §8): the page that wins coaches. How it works, what they get, what it costs
// (the plans' real limits from tenancy-api, the fee and refund window from /config.json, the same
// settings ordering-api applies) and the questions coaches ask before they sign up.
export function Sell() {
  useTitle('Sell your programs')
  const { api, config } = useApp()
  const fee = config.platformFeePercent ?? 10
  const refundDays = config.refundWindowDays ?? 14
  const plans = useQuery({
    queryKey: ['plans'],
    queryFn: async () => (await api.GET('/api/v1/plans')).data ?? [],
    staleTime: 60 * 60_000,
  })

  return (
    <>
      <section className="relative isolate overflow-hidden bg-ink text-ink-foreground">
        <div
          aria-hidden
          className="absolute inset-0 -z-10"
          style={{
            background:
              'radial-gradient(60% 90% at 90% 0%, color-mix(in oklch, var(--volt) 35%, transparent), transparent 70%), radial-gradient(40% 70% at 0% 100%, color-mix(in oklch, var(--volt) 12%, transparent), transparent 70%)',
          }}
        />
        <div className="container-page grid gap-10 py-14 sm:py-20 lg:grid-cols-[1.3fr_1fr] lg:items-center">
          <div className="space-y-6">
            <p className="text-xs font-semibold tracking-[0.25em] text-volt uppercase">For coaches</p>
            <h1 className="text-4xl leading-[0.95] font-black tracking-tight uppercase sm:text-6xl" style={{ fontStretch: '78%' }}>
              Sell your training programs <span className="text-volt">online.</span>
            </h1>
            <p className="max-w-xl text-lg text-ink-foreground/75">
              Your own storefront, payments straight to your bank, and customers who keep coming back. Set up in minutes; no monthly fee to start.
            </p>
            <div className="flex flex-wrap gap-3">
              <Button asChild size="lg" className="bg-volt text-volt-foreground hover:bg-volt/90">
                <Link to="/studio/new">
                  Open your studio <ArrowRight aria-hidden />
                </Link>
              </Button>
              <Button asChild size="lg" variant="outline" className="border-white/30 bg-transparent text-ink-foreground hover:bg-white/10">
                <a href="#pricing">See pricing</a>
              </Button>
            </div>
          </div>
          <ul className="grid gap-3 text-sm">
            {[
              { icon: Wallet, text: `You keep ${100 - fee}% of every sale` },
              { icon: CreditCard, text: 'Payments and payouts by Stripe' },
              { icon: Store, text: 'Your name, logo and colors' },
              { icon: Users, text: 'Customers buy from many coaches in one checkout' },
            ].map((item) => (
              <li key={item.text} className="flex items-center gap-3 rounded-2xl bg-white/5 p-4 ring-1 ring-white/10">
                <span className="flex size-9 shrink-0 items-center justify-center rounded-xl bg-volt text-volt-foreground">
                  <item.icon className="size-4" aria-hidden />
                </span>
                <span className="font-medium">{item.text}</span>
              </li>
            ))}
          </ul>
        </div>
      </section>

      <div className="container-page space-y-20 py-14 sm:py-20">
        <SellSection eyebrow="How it works" title="From sign-up to your first sale">
          <ol className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {[
              { icon: Rocket, title: 'Open your studio', text: 'Pick a name and an address for your storefront. It takes two minutes.' },
              { icon: Wallet, title: 'Set up payouts', text: 'Stripe checks who you are and where to pay you. Your money goes to your bank.' },
              { icon: BadgeCheck, title: 'Create programs', text: 'Weeks of workouts with instructions and video links, a price, a cover, and a free preview.' },
              { icon: Share2, title: 'Share and sell', text: 'Share your storefront link. Customers pay once and keep every workout.' },
            ].map((step, i) => (
              <li key={step.title} className="relative space-y-3 rounded-3xl border bg-card p-6">
                <span className="absolute top-5 right-5 font-heading text-4xl font-black text-muted-foreground/20">{i + 1}</span>
                <span className="flex size-11 items-center justify-center rounded-2xl bg-volt text-volt-foreground">
                  <step.icon className="size-5" aria-hidden />
                </span>
                <h3 className="text-lg font-bold">{step.title}</h3>
                <p className="text-sm text-muted-foreground">{step.text}</p>
              </li>
            ))}
          </ol>
        </SellSection>

        <SellSection eyebrow="What you get" title="Everything to sell programs, nothing to maintain">
          <ul className="grid gap-x-8 gap-y-6 sm:grid-cols-2 lg:grid-cols-3">
            {[
              { icon: Palette, title: 'Your storefront', text: 'Your logo or photo, your colors, your story and links to your Instagram and YouTube.' },
              { icon: Eye, title: 'A free preview', text: 'Show one workout in full and an outline of the rest, so customers know what they buy.' },
              { icon: LineChart, title: 'Sales at a glance', text: 'Sales, earnings and customers this month, and an email for every sale.' },
              { icon: Users, title: 'Your team', text: 'Invite staff to build programs with you. You stay the owner.' },
              { icon: RotateCcw, title: 'Refunds handled', text: `Refund a sale in one click within ${refundDays} days; the customer is told by email.` },
              { icon: Mail, title: 'Customers reach you', text: 'Add a contact address; customers see it on your storefront and their orders.' },
            ].map((feature) => (
              <li key={feature.title} className="flex gap-4">
                <span className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-muted">
                  <feature.icon className="size-5" aria-hidden />
                </span>
                <span>
                  <span className="block font-semibold">{feature.title}</span>
                  <span className="text-sm text-muted-foreground">{feature.text}</span>
                </span>
              </li>
            ))}
          </ul>
        </SellSection>

        <SellSection id="pricing" eyebrow="Pricing" title={`No monthly fee. ${fee}% per sale.`}>
          <p className="max-w-2xl text-muted-foreground">
            Athlos keeps {fee}% of each sale and pays Stripe's card fees out of it: you receive {100 - fee}% of the price. Programs cost from €10.
          </p>
          <div className="grid gap-4 md:grid-cols-2">
            {(plans.data ?? []).map((plan) => (
              <div key={plan.name} className={plan.name === 'Pro' ? 'rounded-3xl border-2 border-primary bg-card p-6 sm:p-8' : 'rounded-3xl border bg-card p-6 sm:p-8'}>
                <p className="text-xs font-semibold tracking-[0.15em] text-muted-foreground uppercase">{plan.name}</p>
                <p className="mt-2 font-heading text-3xl font-black">{plan.name === 'Free' ? 'Free' : 'On request'}</p>
                <p className="text-sm text-muted-foreground">{plan.name === 'Free' ? 'Everything you need to start.' : 'For growing coaches and teams, from the Athlos team.'}</p>
                <ul className="mt-5 space-y-2 text-sm">
                  {[
                    `Up to ${Number(plan.maxPrograms)} programs`,
                    `${Number(plan.maxStaff)} staff ${Number(plan.maxStaff) === 1 ? 'member' : 'members'}`,
                    `${Number(plan.maxMediaGigabytes)} GB for covers and images`,
                    `${fee}% per sale`,
                  ].map((line) => (
                    <li key={line} className="flex items-center gap-2">
                      <Check className="size-4 shrink-0 text-primary" aria-hidden /> {line}
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </div>
        </SellSection>

        <SellSection eyebrow="Questions" title="Before you start">
          <dl className="grid gap-4 md:grid-cols-2">
            {[
              { q: 'How do I get paid?', a: "Through Stripe, the payment company. You set up a Stripe account from your studio; your share of each sale goes to it and on to your bank on Stripe's payout schedule." },
              { q: 'What does it cost?', a: `Nothing a month on the Free plan. Athlos keeps ${fee}% of each sale, card fees included.` },
              { q: 'Can customers get their money back?', a: `You can refund any sale within ${refundDays} days from your studio. The customer gets their money back and loses access; your share is taken back.` },
              { q: 'Do I need a website?', a: 'No. Your storefront is your page on Athlos, with your name, logo, colors and links. Share its address anywhere.' },
              { q: 'What can I sell?', a: 'Training programs: weeks of workouts with instructions and links to your videos (YouTube or Vimeo), at a price you set from €10.' },
              { q: 'Can I stop any time?', a: `Yes. You can delete your workspace from its settings once no sale can still be refunded (${refundDays} days after the last one).` },
            ].map((item) => (
              <div key={item.q} className="rounded-3xl border bg-card p-6">
                <dt className="font-semibold">{item.q}</dt>
                <dd className="mt-2 text-sm text-muted-foreground">{item.a}</dd>
              </div>
            ))}
          </dl>
        </SellSection>

        <section className="relative isolate overflow-hidden rounded-3xl bg-volt px-6 py-10 text-volt-foreground sm:px-12 sm:py-14">
          <div aria-hidden className="absolute -right-16 -bottom-24 -z-10 size-80 rounded-full border-[40px] border-volt-foreground/10" />
          <div className="flex flex-wrap items-center justify-between gap-6">
            <h2 className="text-3xl leading-tight font-black tracking-tight uppercase sm:text-5xl" style={{ fontStretch: '80%' }}>
              Your studio is two minutes away
            </h2>
            <Button asChild size="lg" className="bg-ink text-ink-foreground hover:bg-ink/85">
              <Link to="/studio/new">
                Open your studio <ArrowRight aria-hidden />
              </Link>
            </Button>
          </div>
        </section>
      </div>
    </>
  )
}

function SellSection({ id, eyebrow, title, children }: { id?: string; eyebrow: string; title: string; children: ReactNode }) {
  const heading = `${eyebrow.toLowerCase().replace(/[^a-z0-9]+/g, '-')}-heading`
  return (
    <section id={id} aria-labelledby={heading} className="scroll-mt-28 space-y-6">
      <div className="space-y-2">
        <p className="text-xs font-semibold tracking-[0.2em] text-muted-foreground uppercase">{eyebrow}</p>
        <h2 id={heading} className="text-3xl font-black tracking-tight sm:text-4xl">
          {title}
        </h2>
      </div>
      {children}
    </section>
  )
}
