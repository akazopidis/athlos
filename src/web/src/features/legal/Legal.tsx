import type { ReactNode } from 'react'
import { Link, NavLink } from 'react-router'
import { useApp } from '@/lib/AppContext'
import { TermsDate } from '@/lib/legal'
import { useTitle } from '@/lib/useTitle'
import { cn } from '@/lib/utils'

// The shop's legal pages (design §5.4): Terms, Privacy and Refunds, linked from the footer and from
// checkout, where the customer accepts the Terms. Athlos is a master's thesis project: the texts say
// what the platform actually does (who sells, who is paid, what data goes where), but they are a
// starting point a lawyer would review before real money is taken, not legal advice.
const Pages = [
  { to: '/legal/terms', label: 'Terms' },
  { to: '/legal/privacy', label: 'Privacy' },
  { to: '/legal/refunds', label: 'Refunds' },
] as const

function LegalPage({ title, children }: { title: string; children: ReactNode }) {
  useTitle(title)
  return (
    <div className="grid gap-10 lg:grid-cols-[12rem_minmax(0,1fr)]">
      <nav aria-label="Legal">
        <ul className="flex gap-1 lg:flex-col">
          {Pages.map((p) => (
            <li key={p.to}>
              <NavLink
                to={p.to}
                className={({ isActive }) =>
                  cn('block rounded-lg px-3 py-2 text-sm font-medium', isActive ? 'bg-muted text-foreground' : 'text-muted-foreground hover:text-foreground')
                }
              >
                {p.label}
              </NavLink>
            </li>
          ))}
        </ul>
      </nav>
      <article className="max-w-prose space-y-6 [&_h2]:pt-2 [&_h2]:text-xl [&_h2]:font-bold [&_li]:ml-5 [&_li]:list-disc [&_p]:text-muted-foreground [&_ul]:space-y-1 [&_ul]:text-muted-foreground">
        <header className="space-y-2">
          <h1 className="text-3xl font-extrabold tracking-tight sm:text-4xl">{title}</h1>
          <p className="text-sm">Last updated {TermsDate}.</p>
        </header>
        {children}
      </article>
    </div>
  )
}

export function Terms() {
  const { config } = useApp()
  const days = config.refundWindowDays ?? 14
  const fee = config.platformFeePercent ?? 10
  return (
    <LegalPage title="Terms of use">
      <p>
        Athlos is a marketplace where independent coaches sell training programs and customers buy them. These terms apply when you use Athlos, buy a
        program or sell one. Athlos is run as a master's thesis project; payments run in Stripe's test mode unless stated otherwise.
      </p>
      <h2>Buying a program</h2>
      <ul>
        <li>You pay Athlos once, in one payment for everything in your cart. Athlos passes each coach their share and keeps a {fee}% platform fee.</li>
        <li>Prices are in euros and include any discount shown in your cart. Stripe processes the payment; Athlos never sees your card details.</li>
        <li>A program you buy is yours to use for personal training. You may not copy, resell or share its content.</li>
        <li>Access starts as soon as the payment is confirmed. You can ask for a refund as described in the <Link to="/legal/refunds" className="underline underline-offset-4">refund policy</Link>.</li>
      </ul>
      <h2>Coaches</h2>
      <ul>
        <li>Each coach is responsible for their programs: their content, their safety advice and their accuracy. Athlos does not check training advice.</li>
        <li>Coaches are paid through their own Stripe account and must give Stripe correct details about who they are.</li>
        <li>A coach may refund a sale within {days} days. If a coach deletes their workspace, customers lose access to that coach's programs.</li>
      </ul>
      <h2>Training safely</h2>
      <p>Exercise carries risk. Check with a doctor before starting a new program, and stop if something hurts. Athlos and its coaches are not medical providers.</p>
      <h2>Reviews</h2>
      <p>
        Only customers who bought a program can review it. Reviews must be honest and about the program. Athlos may hide reviews that are abusive,
        off-topic or contain personal data, and anyone signed in can report one.
      </p>
      <h2>Your account</h2>
      <p>
        You sign in with an account managed by Microsoft Entra on Athlos' behalf. You can delete your account at any time from your account settings; see
        the <Link to="/legal/privacy" className="underline underline-offset-4">privacy notice</Link> for what happens to your data.
      </p>
      <h2>Changes</h2>
      <p>If these terms change, the date above changes and you are asked to accept the new version at your next checkout.</p>
    </LegalPage>
  )
}

export function Privacy() {
  return (
    <LegalPage title="Privacy notice">
      <p>This notice explains what Athlos stores about you, why, who else receives it, and how you control it.</p>
      <h2>What we store</h2>
      <ul>
        <li>Your account: your name and email address from your sign-in, and your sign-in id.</li>
        <li>Your purchases: what you bought, from which coach, when, for how much, and any refund.</li>
        <li>Your training: the programs you own, the workouts you ticked off, the programs you saved and your reviews.</li>
        <li>If you coach: your workspace, its branding and profile, your team and your Stripe account id.</li>
      </ul>
      <h2>Why</h2>
      <p>To run the shop: to let you sign in, take payments, give you the programs you bought, pay coaches, send the emails each step needs, and keep the books.</p>
      <h2>Who else receives it</h2>
      <ul>
        <li>Microsoft (Entra External ID for sign-in, Azure for hosting and email), in the EU (Italy North).</li>
        <li>Stripe, for payments and coach payouts.</li>
        <li>The coach you buy from sees your email address with the sale, so they can reach you about it. Coaches never see your card details.</li>
        <li>Reviews are public, under your first name and initial.</li>
      </ul>
      <h2>How long</h2>
      <p>
        As long as your account exists. When you delete it, your programs, progress, saved programs and reviews are erased; your orders stay for the coaches'
        accounting, with your email address removed.
      </p>
      <h2>Your rights</h2>
      <ul>
        <li>Download everything we hold about you from your account settings ("Download my data").</li>
        <li>Delete your account from the same page.</li>
        <li>Ask a coach about a purchase through the contact address on their storefront.</li>
      </ul>
      <h2>Cookies</h2>
      <p>Athlos uses no tracking or advertising cookies. Your browser keeps your sign-in and your cart, which the shop needs to work.</p>
    </LegalPage>
  )
}

export function Refunds() {
  const { config } = useApp()
  const days = config.refundWindowDays ?? 14
  return (
    <LegalPage title="Refund policy">
      <p>Every program can be refunded within {days} days of purchase.</p>
      <h2>How to get a refund</h2>
      <ul>
        <li>Contact the coach through the contact address on their storefront, or on the order in "Orders".</li>
        <li>The coach refunds the purchase from their studio within {days} days of payment. You get the full amount you paid back on your card and an email confirming it.</li>
        <li>Your access to the program ends with the refund, and your review of it is removed.</li>
      </ul>
      <h2>After {days} days</h2>
      <p>Purchases are final after {days} days. If a payment was made without your permission, contact your bank; a dispute the coach loses also refunds the purchase.</p>
      <h2>Your right of withdrawal</h2>
      <p>
        EU law gives you 14 days to withdraw from an online purchase. For digital content you can use at once, that right can end when access starts; Athlos
        keeps the {days}-day refund anyway.
      </p>
    </LegalPage>
  )
}
