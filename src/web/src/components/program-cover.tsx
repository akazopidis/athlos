import { Dumbbell } from 'lucide-react'
import { cn } from '@/lib/utils'

// A program's cover image (a short-lived link from catalog-api), or a neutral placeholder.
export function ProgramCover({ url, className }: { url?: string | null; className?: string }) {
  return url ? (
    <img src={url} alt="" className={cn('aspect-video w-full object-cover', className)} />
  ) : (
    <div className={cn('flex aspect-video w-full items-center justify-center bg-muted text-muted-foreground', className)}>
      <Dumbbell className="size-8" aria-hidden />
    </div>
  )
}
