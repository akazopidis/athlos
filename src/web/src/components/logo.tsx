import { cn } from '@/lib/utils'

// The Athlos mark (a volt tile with a rising peak, the same as public/favicon.svg) and wordmark.
export function Logo({ className }: { className?: string }) {
  return (
    <span className={cn('flex items-center gap-2', className)}>
      <svg viewBox="0 0 32 32" className="size-8 shrink-0" aria-hidden>
        <rect width="32" height="32" rx="9" fill="var(--volt)" />
        <path d="M7.5 22.5 14 10l4 7.5 2.5-4 4 9" fill="none" stroke="var(--volt-foreground)" strokeWidth="3.2" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
      <span className="font-heading text-xl font-extrabold tracking-tight" style={{ fontStretch: '85%' }}>
        ATHLOS
      </span>
    </span>
  )
}
