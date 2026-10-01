import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { useApp } from '../AppContext.tsx'

export function MyWorkspaces() {
  const { api } = useApp()
  const memberships = useQuery({
    queryKey: ['memberships'],
    queryFn: async () => (await api.GET('/api/v1/me/memberships')).data ?? [],
  })

  if (memberships.isPending) return <p>Loading…</p>

  return (
    <section>
      <h2>My workspaces</h2>
      {memberships.data?.length ? (
        <ul>
          {memberships.data.map((m) => (
            <li key={m.tenantId}>
              <Link to={`/studio/${m.slug}`}>{m.displayName}</Link> ({m.role})
            </li>
          ))}
        </ul>
      ) : (
        <p>
          None yet. <Link to="/studio/new">Open a workspace</Link>
        </p>
      )}
    </section>
  )
}
