import { ArrowRight } from 'lucide-react'
import { Link } from 'react-router'
import { CoachAvatar } from '@/components/coach-profile'
import type { MarketplaceCoach } from './useMarketplaceCoaches.ts'

// Coaches as cards: logo or photo, name, how many programs, the start of their bio.
export function CoachGrid({ coaches }: { coaches: MarketplaceCoach[] }) {
  return (
    <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
      {coaches.map((c) => (
        <li key={c.slug}>
          <Link
            to={`/c/${c.slug}`}
            className="group flex h-full flex-col gap-4 rounded-3xl border bg-card p-5 transition-shadow outline-none hover:shadow-lg focus-visible:ring-3 focus-visible:ring-ring/50"
          >
            <div className="flex items-center gap-3">
              <CoachAvatar name={c.displayName} profile={c.branding} className="size-14 text-xl" />
              <div className="min-w-0">
                <p className="truncate font-semibold underline-offset-4 group-hover:underline">{c.displayName}</p>
                <p className="text-sm text-muted-foreground">
                  {c.programs} {c.programs === 1 ? 'program' : 'programs'}
                </p>
              </div>
            </div>
            {c.branding.bio && <p className="line-clamp-3 text-sm text-muted-foreground">{c.branding.bio}</p>}
            <span className="mt-auto inline-flex items-center gap-1 text-sm font-medium text-primary">
              Visit storefront <ArrowRight className="size-4" aria-hidden />
            </span>
          </Link>
        </li>
      ))}
    </ul>
  )
}
