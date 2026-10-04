// Per-environment settings come from the gateway at runtime (GET /config.json), never from build-time
// variables, so the same image runs in dev, staging and prod (design §8.1).
export interface AppConfig {
  entraClientId: string | null
  entraAuthority: string | null
  apiScope: string | null
  version: string
}

export async function loadConfig(): Promise<AppConfig> {
  const response = await fetch('/config.json')
  if (!response.ok) throw new Error(`GET /config.json failed: ${response.status}`)
  return (await response.json()) as AppConfig
}

export function signInConfigured(config: AppConfig): config is AppConfig & {
  entraClientId: string
  entraAuthority: string
  apiScope: string
} {
  return Boolean(config.entraClientId && config.entraAuthority && config.apiScope)
}
