import { cn } from '@/lib/utils'

// A program's cover image (a short-lived link from catalog-api). Without one, a generated cover:
// a gradient whose hue comes from the title, so each program looks distinct and stays the same.
export function ProgramCover({ url, title, className }: { url?: string | null; title?: string; className?: string }) {
  if (url) return <img src={url} alt="" loading="lazy" className={cn('aspect-[4/3] w-full bg-muted object-cover', className)} />

  const seed = [...(title ?? '')].reduce((hash, c) => (hash * 31 + c.charCodeAt(0)) >>> 0, 7)
  const hue = seed % 360
  return (
    <div
      aria-hidden
      className={cn('relative flex aspect-[4/3] w-full items-end overflow-hidden', className)}
      style={{
        background: `radial-gradient(90% 80% at 85% 10%, oklch(0.75 0.17 ${hue}) 0%, transparent 60%), linear-gradient(135deg, oklch(0.32 0.09 ${hue}) 0%, oklch(0.18 0.03 ${(hue + 40) % 360}) 100%)`,
      }}
    >
      <span className="pointer-events-none absolute -right-2 -bottom-6 font-heading text-[7rem] leading-none font-black text-white/10 select-none">
        {(title ?? '').slice(0, 2).toUpperCase()}
      </span>
    </div>
  )
}
