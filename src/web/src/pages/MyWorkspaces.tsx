import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { useApp } from '../AppContext.tsx'

export function MyWorkspaces() {
  const { api, msal } = useApp()
  const signedIn = Boolean(msal?.getActiveAccount())
  const memberships = useQuery({
    queryKey: ['memberships'],
    queryFn: async () => (await api.GET('/api/v1/me/memberships')).data ?? [],
    enabled: signedIn,
  })

  if (!signedIn) return <p>Sign in to see your workspaces.</p>
  if (memberships.isPending) return <p>Loading…</p>

  return (
    <section>
      <h1>My workspaces</h1>
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
