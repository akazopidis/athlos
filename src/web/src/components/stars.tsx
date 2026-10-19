import { Star } from 'lucide-react'
import { formatRating } from '@/lib/rating'
import { cn } from '@/lib/utils'

// Ratings (design §2.4): 1 to 5 stars. Amber is the shop's rating color in both themes; the numbers
// next to the stars carry the meaning in text.
const filled = 'fill-amber-400 text-amber-400'
const empty = 'fill-muted text-muted-foreground/30'

// Five stars filled to the value (4.5 fills four and a half), read out as "4.5 out of 5 stars".
export function Stars({ value, className = 'size-4' }: { value: number; className?: string }) {
  const percent = Math.max(0, Math.min(100, (value / 5) * 100))
  return (
    <span role="img" aria-label={`${formatRating(value)} out of 5 stars`} className="relative inline-flex shrink-0">
      <span className="flex" aria-hidden>
        {[0, 1, 2, 3, 4].map((i) => (
          <Star key={i} className={cn(className, empty)} />
        ))}
      </span>
      <span className="absolute inset-0 flex overflow-hidden" style={{ width: `${percent}%` }} aria-hidden>
        {[0, 1, 2, 3, 4].map((i) => (
          <Star key={i} className={cn(className, filled, 'shrink-0')} />
        ))}
      </span>
    </span>
  )
}

// "★ 4.6 (12)" for product cards; nothing until a program has a review.
export function RatingBadge({ average, count, className }: { average: number; count: number; className?: string }) {
  if (count === 0) return null
  return (
    <span className={cn('inline-flex items-center gap-1 text-sm', className)}>
      <Star className={cn('size-3.5', filled)} aria-hidden />
      <span className="font-semibold tabular-nums">{formatRating(average)}</span>
      <span className="text-muted-foreground tabular-nums">
        ({count}
        <span className="sr-only"> {count === 1 ? 'review' : 'reviews'}</span>)
      </span>
    </span>
  )
}

// Choosing a rating: five radio buttons drawn as stars, so keyboards and screen readers get a real
// radio group (arrow keys move between them).
export function StarInput({ value, onChange, name, label }: { value: number; onChange: (rating: number) => void; name: string; label: string }) {
  return (
    <fieldset className="space-y-1">
      <legend className="text-sm font-medium">{label}</legend>
      <div className="flex gap-1">
        {[1, 2, 3, 4, 5].map((n) => (
          <label key={n} className="cursor-pointer rounded-md p-1 has-[:focus-visible]:ring-3 has-[:focus-visible]:ring-ring/50">
            <input type="radio" name={name} value={n} checked={value === n} onChange={() => onChange(n)} className="sr-only" />
            <Star className={cn('size-8 transition-colors', n <= value ? filled : empty)} aria-hidden />
            <span className="sr-only">
              {n} {n === 1 ? 'star' : 'stars'}
            </span>
          </label>
        ))}
      </div>
    </fieldset>
  )
}
