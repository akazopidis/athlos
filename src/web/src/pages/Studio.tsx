import { Link, NavLink, Outlet } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Loading, PageHeader } from '@/components/page'
import { cn } from '@/lib/utils'
import { useStudio } from './studio/useStudio.ts'

// The coach dashboard (design §8, "/studio/{slug}": programs, sales, settings, Stripe onboarding).
// A layout: the workspace header and tabs; the pages render in the outlet.
export function Studio() {
  const { slug, workspace } = useStudio()

  if (workspace.isPending) return <Loading lines={4} />
  if (!workspace.data) return <p>Workspace not found.</p>

  const { displayName, plan, status } = workspace.data
  return (
    <div className="space-y-8">
      <PageHeader title={displayName} description="Studio">
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant="outline">{plan} plan</Badge>
          <Badge variant={status === 'Active' ? 'secondary' : 'destructive'}>{status}</Badge>
          <Button asChild variant="outline" size="sm">
            <Link to={`/c/${slug}`}>View storefront</Link>
          </Button>
        </div>
      </PageHeader>
      <nav className="flex gap-1 border-b" aria-label="Studio">
        {[
          { to: '', label: 'Overview', end: true },
          { to: 'sales', label: 'Sales' },
          { to: 'settings', label: 'Settings' },
        ].map((tab) => (
          <NavLink
            key={tab.label}
            to={tab.to}
            end={tab.end}
            className={({ isActive }) =>
              cn(
                '-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors',
                isActive ? 'border-primary text-foreground' : 'border-transparent text-muted-foreground hover:text-foreground',
              )
            }
          >
            {tab.label}
          </NavLink>
        ))}
      </nav>
      <Outlet />
    </div>
  )
}
