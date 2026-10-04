import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { Logo } from './Logo.tsx'

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
    <footer className="mt-16 border-t bg-muted/40">
      <div className="container-page flex flex-col gap-8 py-10 sm:flex-row sm:items-start sm:justify-between">
        <div className="max-w-xs space-y-3">
          <Logo />
          <p className="text-sm text-muted-foreground">Training programs from independent coaches. Buy once, train at your own pace.</p>
        </div>
        <nav className="grid grid-cols-2 gap-x-12 gap-y-2 text-sm" aria-label="Footer">
          <p className="font-semibold">Train</p>
          <p className="font-semibold">Coach</p>
          <Link to="/" className="text-muted-foreground hover:text-foreground">
            Programs
          </Link>
          <Link to="/studio/new" className="text-muted-foreground hover:text-foreground">
            Open your studio
          </Link>
          <Link to="/me" className="text-muted-foreground hover:text-foreground">
            My account
          </Link>
        </nav>
      </div>
      <div className="border-t">
        <p className="container-page py-4 text-xs text-muted-foreground">Athlos {version ?? ''}</p>
      </div>
    </footer>
  )
}
