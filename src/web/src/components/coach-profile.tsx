import { AtSign, Globe, Mail, Play } from 'lucide-react'
import type { components } from '@/api/tenancy'
import { cn } from '@/lib/utils'

export type CoachProfile = components['schemas']['CoachProfile']

// The coach's logo or photo, or their initial on their color.
export function CoachAvatar({ name, profile, className }: { name: string; profile?: CoachProfile; className?: string }) {
  if (profile?.logoUrl) return <img src={profile.logoUrl} alt="" className={cn('shrink-0 rounded-2xl bg-white object-cover', className)} />
  return (
    <div
      aria-hidden
      className={cn('flex shrink-0 items-center justify-center rounded-2xl font-heading font-extrabold text-white', className)}
      style={{ backgroundColor: profile?.primaryColor ?? 'var(--ink)' }}
    >
      {(name || '?').slice(0, 1).toUpperCase()}
    </div>
  )
}

// The coach's links elsewhere and their contact address, as small labelled buttons.
export function CoachLinks({ name, profile, className }: { name: string; profile?: CoachProfile; className?: string }) {
  const links = [
    { href: profile?.instagramUrl, label: 'Instagram', icon: AtSign },
    { href: profile?.youTubeUrl, label: 'YouTube', icon: Play },
    { href: profile?.websiteUrl, label: 'Website', icon: Globe },
    { href: profile?.contactEmail ? `mailto:${profile.contactEmail}` : null, label: `Contact ${name}`, icon: Mail },
  ].filter((l) => l.href)
  if (links.length === 0) return null

  return (
    <ul className={cn('flex flex-wrap gap-2', className)}>
      {links.map((l) => (
        <li key={l.label}>
          <a
            href={l.href!}
            target={l.href!.startsWith('mailto:') ? undefined : '_blank'}
            rel="noreferrer"
            className="inline-flex items-center gap-1.5 rounded-full border bg-background px-3 py-1.5 text-sm font-medium transition-colors hover:bg-muted"
          >
            <l.icon className="size-4" aria-hidden /> {l.label}
          </a>
        </li>
      ))}
    </ul>
  )
}
