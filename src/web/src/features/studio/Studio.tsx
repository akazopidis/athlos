import { ExternalLink } from 'lucide-react'
import { Link, NavLink, Outlet } from 'react-router'
import { Loading, Notice } from '@/components/page'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { readableOn } from '@/lib/color'
import { useTitle } from '@/lib/useTitle'
import { cn } from '@/lib/utils'
import { useStudio } from './useStudio.ts'

const tabs = [
  { to: '', label: 'Overview', end: true },
  { to: 'sales', label: 'Sales' },
  { to: 'settings', label: 'Settings' },
]

// The coach dashboard (design §8, "/studio/{slug}": programs, sales, settings, Stripe onboarding).
// A layout: the workspace header and tabs; the pages render in the outlet.
export function Studio() {
  const { slug, workspace } = useStudio()

  useTitle(workspace.data ? `${workspace.data.displayName} studio` : null)

  if (workspace.isPending) return <Loading lines={4} />
  if (!workspace.data) return <Notice title="Workspace not found">Sign in with an account that coaches in this workspace.</Notice>

  const { displayName, plan, status, branding } = workspace.data
  return (
    <div className="space-y-8">
      <div className="flex flex-wrap items-center gap-4">
        <span
          className="flex size-14 shrink-0 items-center justify-center rounded-2xl bg-ink font-heading text-2xl font-extrabold text-ink-foreground"
          style={branding.primaryColor ? { backgroundColor: branding.primaryColor, color: readableOn(branding.primaryColor) } : undefined}
          aria-hidden
        >
          {displayName.slice(0, 1).toUpperCase()}
        </span>
        <div className="min-w-0 flex-1">
          <p className="text-xs font-semibold tracking-[0.2em] text-muted-foreground uppercase">Studio</p>
          <h1 className="truncate text-3xl font-extrabold tracking-tight">{displayName}</h1>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant="outline">{plan} plan</Badge>
          <Badge variant={status === 'Active' ? 'secondary' : 'destructive'}>{status}</Badge>
          <Button asChild variant="outline" size="sm">
            <Link to={`/c/${slug}`}>
              View storefront <ExternalLink aria-hidden />
            </Link>
          </Button>
        </div>
      </div>
      <nav className="-mx-4 overflow-x-auto border-b px-4 sm:mx-0 sm:px-0" aria-label="Studio">
        <div className="flex gap-1">
          {tabs.map((tab) => (
            <NavLink
              key={tab.label}
              to={tab.to}
              end={tab.end}
              className={({ isActive }) =>
                cn(
                  '-mb-px border-b-2 px-4 py-3 text-sm font-semibold whitespace-nowrap transition-colors',
                  isActive ? 'border-foreground text-foreground' : 'border-transparent text-muted-foreground hover:text-foreground',
                )
              }
            >
              {tab.label}
            </NavLink>
          ))}
        </div>
      </nav>
      <Outlet />
    </div>
  )
}
