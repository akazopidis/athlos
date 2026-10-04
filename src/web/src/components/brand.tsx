import type { CSSProperties, ReactNode } from 'react'
import { readableOn } from '@/lib/color'
import { cn } from '@/lib/utils'

// The coach's color takes over the primary color (buttons, links, focus rings) inside a storefront
// and its program pages: the visible "your own workspace" part of the thesis (design §8).
function brandStyle(color?: string | null): CSSProperties | undefined {
  if (!color) return undefined
  return { '--primary': color, '--primary-foreground': readableOn(color), '--ring': color } as CSSProperties
}

export function BrandScope({ color, className, children }: { color?: string | null; className?: string; children: ReactNode }) {
  return (
    <div style={brandStyle(color)} className={className}>
      {children}
    </div>
  )
}

// The storefront's header: the coach's logo (or initial) and name on their color.
export function StorefrontBanner({
  name,
  logoUrl,
  color,
  children,
  className,
  innerClassName,
  heading: Heading = 'h1',
}: {
  name: string
  logoUrl?: string | null
  color?: string | null
  children?: ReactNode
  className?: string
  innerClassName?: string
  // A preview inside another page (studio settings) is not that page's heading.
  heading?: 'h1' | 'p'
}) {
  const large = Heading === 'h1'
  const background = color ?? 'var(--ink)'
  const text = color ? readableOn(color) : 'var(--ink-foreground)'
  return (
    <div
      className={cn('relative isolate overflow-hidden', className)}
      style={{ backgroundColor: background, color: text }}
    >
      {/* Depth without an image: a soft light from the top right and a darker base. */}
      <div
        aria-hidden
        className="absolute inset-0 -z-10"
        style={{ background: 'radial-gradient(120% 90% at 100% 0%, rgb(255 255 255 / 0.22), transparent 55%), linear-gradient(to top, rgb(0 0 0 / 0.25), transparent 70%)' }}
      />
      <div className={cn('flex items-center gap-4 sm:gap-6', innerClassName)}>
        {logoUrl ? (
          <img src={logoUrl} alt="" className={cn('size-16 shrink-0 rounded-2xl bg-white object-cover shadow-lg ring-4 ring-white/20', large && 'sm:size-24')} />
        ) : (
          <div
            aria-hidden
            className={cn(
              'flex size-16 shrink-0 items-center justify-center rounded-2xl bg-white/15 font-heading text-3xl font-extrabold ring-4 ring-white/20',
              large && 'sm:size-24 sm:text-5xl',
            )}
          >
            {(name || '?').slice(0, 1).toUpperCase()}
          </div>
        )}
        <div className="min-w-0 space-y-1">
          <p className="text-xs font-semibold tracking-[0.2em] uppercase opacity-75">Coach on Athlos</p>
          <Heading className={cn('truncate font-heading font-extrabold tracking-tight', large ? 'text-3xl sm:text-5xl' : 'text-2xl')}>{name || 'Your workspace'}</Heading>
          {children}
        </div>
      </div>
    </div>
  )
}
