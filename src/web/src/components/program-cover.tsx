import { CategoryIcon } from './category-icon.tsx'
import { cn } from '@/lib/utils'

// A program's cover image (a short-lived link from catalog-api). Without one, a generated cover: a
// gradient whose hue comes from the title, so each program looks distinct and stays the same, with its
// category's icon.
export function ProgramCover({ url, title, category, className }: { url?: string | null; title?: string; category?: string | null; className?: string }) {
  if (url) return <img src={url} alt="" loading="lazy" className={cn('aspect-[4/3] w-full bg-muted object-cover', className)} />

  const seed = [...(title ?? '')].reduce((hash, c) => (hash * 31 + c.charCodeAt(0)) >>> 0, 7)
  const hue = seed % 360
  return (
    <div
      aria-hidden
      className={cn('relative flex aspect-[4/3] w-full items-center justify-center overflow-hidden', className)}
      style={{
        background: `radial-gradient(90% 80% at 85% 10%, oklch(0.75 0.17 ${hue}) 0%, transparent 60%), linear-gradient(135deg, oklch(0.36 0.1 ${hue}) 0%, oklch(0.2 0.04 ${(hue + 40) % 360}) 100%)`,
      }}
    >
      <div className="absolute inset-0 opacity-[0.08] [background-image:repeating-linear-gradient(115deg,white_0_1px,transparent_1px_22px)]" />
      <CategoryIcon category={category} className="absolute -right-6 -bottom-8 size-2/3 text-white/10" strokeWidth={1.25} />
      <span className="relative flex size-16 items-center justify-center rounded-2xl bg-white/15 shadow-lg ring-1 ring-white/25 backdrop-blur-sm sm:size-20">
        <CategoryIcon category={category} className="size-8 text-white sm:size-10" strokeWidth={1.75} />
      </span>
    </div>
  )
}
