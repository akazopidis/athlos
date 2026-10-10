import { useQuery } from '@tanstack/react-query'
import { useApp } from './AppContext.tsx'
import { getAccessToken } from './auth.ts'

export const PlatformAdminRole = 'Platform.Admin'

// The Entra app roles in an access token's "roles" claim. Only for showing or hiding the admin
// entry: tenancy-api checks the signed token itself (design §5).
export function appRolesOf(accessToken: string): string[] {
  try {
    const payload = accessToken.split('.')[1]!.replace(/-/g, '+').replace(/_/g, '/')
    const claims: unknown = JSON.parse(atob(payload))
    const roles = typeof claims === 'object' && claims !== null && 'roles' in claims ? claims.roles : undefined
    return Array.isArray(roles) ? roles.filter((r): r is string => typeof r === 'string') : []
  } catch {
    return []
  }
}

// Whether the signed-in user holds the Platform.Admin app role (design §1).
export function usePlatformAdmin(): boolean {
  const { msal, config } = useApp()
  const account = msal?.getActiveAccount()
  const admin = useQuery({
    queryKey: ['platform-admin', account?.localAccountId],
    queryFn: async () => {
      const token = await getAccessToken(msal, config.apiScope)
      return token ? appRolesOf(token).includes(PlatformAdminRole) : false
    },
    enabled: Boolean(account),
    staleTime: Infinity,
  })
  return admin.data ?? false
}
