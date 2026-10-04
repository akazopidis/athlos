import { useQuery } from '@tanstack/react-query'
import { ChevronRight, Plus, Store } from 'lucide-react'
import { Link } from 'react-router'
import { Empty, Loading, Section } from '@/components/page'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'

export function MyWorkspaces() {
  const { api } = useApp()
  const memberships = useQuery({
    queryKey: ['memberships'],
    queryFn: async () => (await api.GET('/api/v1/me/memberships')).data ?? [],
  })

  return (
    <Section
      title="My workspaces"
      action={
        <Button asChild variant="outline" size="sm">
          <Link to="/studio/new">
            <Plus aria-hidden /> Open a workspace
          </Link>
        </Button>
      }
    >
      {memberships.isPending ? (
        <Loading />
      ) : memberships.data?.length ? (
        <ul className="grid gap-3 sm:grid-cols-2">
          {memberships.data.map((m) => (
            <li key={m.tenantId}>
              <Link
                to={`/studio/${m.slug}`}
                className="group flex items-center gap-4 rounded-2xl border bg-card p-4 transition-shadow outline-none hover:shadow-md focus-visible:ring-3 focus-visible:ring-ring/50"
              >
                <span className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-primary font-heading text-lg font-extrabold text-primary-foreground" aria-hidden>
                  {m.displayName.slice(0, 1).toUpperCase()}
                </span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-semibold underline-offset-4 group-hover:underline">{m.displayName}</span>
                  <span className="text-sm text-muted-foreground">Studio</span>
                </span>
                <Badge variant="outline">{m.role}</Badge>
                <ChevronRight className="size-4 text-muted-foreground" aria-hidden />
              </Link>
            </li>
          ))}
        </ul>
      ) : (
        <Empty icon={<Store />}>You don't coach in a workspace yet. Open one to sell your own programs.</Empty>
      )}
    </Section>
  )
}
