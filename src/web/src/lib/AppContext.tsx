import { createContext, useContext } from 'react'
import type { Api } from '@/api/client'
import type { Auth } from './auth.ts'
import type { AppConfig } from './config.ts'

export interface AppServices {
  config: AppConfig
  // null when sign-in is not configured (the app runs signed out).
  auth: Auth | null
  api: Api
}

export const AppContext = createContext<AppServices | null>(null)

export function useApp(): AppServices {
  const services = useContext(AppContext)
  if (!services) throw new Error('useApp must be used inside AppContext')
  return services
}
