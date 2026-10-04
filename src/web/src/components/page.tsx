import type { ReactNode } from 'react'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'

// Small building blocks every page uses, so pages read the same.

export function PageHeader({ eyebrow, title, description, children }: { eyebrow?: ReactNode; title: ReactNode; description?: ReactNode; children?: ReactNode }) {
  return (
    <div className="mb-8 flex flex-wrap items-end justify-between gap-4 sm:mb-10">
      <div className="min-w-0 space-y-2">
        {eyebrow && <p className="text-xs font-semibold tracking-[0.2em] text-muted-foreground uppercase">{eyebrow}</p>}
        <h1 className="text-3xl font-extrabold tracking-tight sm:text-4xl">{title}</h1>
        {description && <p className="max-w-2xl text-muted-foreground">{description}</p>}
      </div>
      {children}
    </div>
  )
}

export function Section({ title, children, action, className }: { title: ReactNode; children: ReactNode; action?: ReactNode; className?: string }) {
  return (
    <section className={cn('space-y-4', className)}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-xl font-bold tracking-tight sm:text-2xl">{title}</h2>
        {action}
      </div>
      {children}
    </section>
  )
}

export function Loading({ lines = 3 }: { lines?: number }) {
  return (
    <div className="space-y-3" aria-busy="true" aria-label="Loading">
      {Array.from({ length: lines }, (_, i) => (
        <Skeleton key={i} className="h-5 w-full max-w-md" />
      ))}
    </div>
  )
}

// Placeholder cards while a grid of programs loads, the same shape as the cards.
export function CardGridSkeleton({ count = 6 }: { count?: number }) {
  return (
    <div className="grid gap-x-5 gap-y-8 sm:grid-cols-2 lg:grid-cols-3" aria-busy="true" aria-label="Loading">
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className="space-y-3">
          <Skeleton className="aspect-[4/3] w-full rounded-2xl" />
          <Skeleton className="h-5 w-3/4" />
          <Skeleton className="h-4 w-1/2" />
        </div>
      ))}
    </div>
  )
}

export function Empty({ icon, title, children, action }: { icon?: ReactNode; title?: ReactNode; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center gap-3 rounded-2xl border border-dashed px-6 py-12 text-center">
      {icon && <div className="flex size-12 items-center justify-center rounded-full bg-muted text-muted-foreground [&_svg]:size-6">{icon}</div>}
      {title && <p className="font-heading text-lg font-bold">{title}</p>}
      {children && <div className="max-w-sm text-sm text-muted-foreground">{children}</div>}
      {action}
    </div>
  )
}

// A centered message for a page that cannot show its content (not found, signed out).
export function Notice({ title, children, action }: { title: ReactNode; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="mx-auto flex max-w-md flex-col items-center gap-3 py-16 text-center">
      <h1 className="text-2xl font-extrabold tracking-tight">{title}</h1>
      {children && <p className="text-muted-foreground">{children}</p>}
      {action}
    </div>
  )
}
