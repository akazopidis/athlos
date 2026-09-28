import type { PublicClientApplication } from '@azure/msal-browser'
import { createContext, useContext } from 'react'
import type { Api } from './api/client.ts'
import type { AppConfig } from './config.ts'

export interface AppServices {
  config: AppConfig
  msal: PublicClientApplication | null
  api: Api
}

export const AppContext = createContext<AppServices | null>(null)

export function useApp(): AppServices {
  const services = useContext(AppContext)
  if (!services) throw new Error('useApp must be used inside AppContext')
  return services
}
