import { useQuery } from '@tanstack/react-query'

async function fetchVersion(): Promise<string> {
  const response = await fetch('/version')
  if (!response.ok) throw new Error(`GET /version failed: ${response.status}`)
  const body = (await response.json()) as { version: string }
  return body.version
}

// Shows the running release, so demos make it obvious which version is live (design §8).
export function Footer() {
  const { data: version } = useQuery({ queryKey: ['version'], queryFn: fetchVersion, staleTime: Infinity })

  return (
    <footer className="border-t">
      <div className="mx-auto max-w-5xl px-4 py-6 text-xs text-muted-foreground">Athlos {version ?? ''}</div>
    </footer>
  )
}
