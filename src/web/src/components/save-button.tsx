import { Heart } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { useSaveProgram, useSavedPrograms } from '@/lib/useSavedPrograms'
import { cn } from '@/lib/utils'

// The heart that saves a program for later (design §2.4). Signed out, it asks the visitor to sign in.
// It shows the new state at once, while the request is on its way.
// compact: a round icon button over a product card's cover; otherwise a labelled button.
export function SaveButton({ programId, title, compact = false, className }: { programId: string; title: string; compact?: boolean; className?: string }) {
  const { msal, config } = useApp()
  const signedIn = Boolean(msal?.getActiveAccount())
  const { ids } = useSavedPrograms()
  const save = useSaveProgram()
  if (!msal) return null

  const saved = save.isPending ? save.variables.save : ids.has(programId)
  const label = saved ? `Remove ${title} from saved` : `Save ${title} for later`
  const onClick = () => (signedIn ? save.mutate({ programId, save: !saved }) : msal.loginRedirect({ scopes: [config.apiScope!] }))
  const heart = <Heart aria-hidden className={cn(saved && 'fill-current text-red-600 dark:text-red-400')} />

  return compact ? (
    <button
      type="button"
      aria-label={label}
      aria-pressed={saved}
      title={save.error?.message ?? (saved ? 'Saved' : 'Save for later')}
      onClick={onClick}
      className={cn(
        'flex size-10 items-center justify-center rounded-full bg-background/90 shadow-sm backdrop-blur transition-colors outline-none hover:bg-background focus-visible:ring-3 focus-visible:ring-ring/50 [&_svg]:size-4.5',
        className,
      )}
    >
      {heart}
    </button>
  ) : (
    <Button variant="outline" size="sm" aria-label={label} aria-pressed={saved} title={save.error?.message} onClick={onClick} className={className}>
      {heart} {saved ? 'Saved' : 'Save'}
    </Button>
  )
}
