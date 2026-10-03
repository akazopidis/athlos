import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader, CardTitle } from '@/components/ui/card'
import { Empty, Loading, Section } from '@/components/page'
import { useApp } from '../AppContext.tsx'

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
          <Link to="/studio/new">Open a workspace</Link>
        </Button>
      }
    >
      {memberships.isPending ? (
        <Loading />
      ) : memberships.data?.length ? (
        <ul className="grid gap-4 sm:grid-cols-2">
          {memberships.data.map((m) => (
            <li key={m.tenantId}>
              <Link to={`/studio/${m.slug}`} className="group block">
                <Card className="transition-shadow group-hover:shadow-md">
                  <CardHeader className="flex-row items-center justify-between">
                    <CardTitle className="group-hover:underline">{m.displayName}</CardTitle>
                    <Badge variant="outline">{m.role}</Badge>
                  </CardHeader>
                </Card>
              </Link>
            </li>
          ))}
        </ul>
      ) : (
        <Empty>You don't coach in a workspace yet.</Empty>
      )}
    </Section>
  )
}
