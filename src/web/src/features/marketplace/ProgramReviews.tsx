import { useInfiniteQuery } from '@tanstack/react-query'
import { MessageSquareReply } from 'lucide-react'
import { Stars } from '@/components/stars'
import { Button } from '@/components/ui/button'
import { useApp } from '@/lib/AppContext'
import { formatRating } from '@/lib/rating'

const PageSize = 5

// A program's reviews on its page (design §2.4, enrollment-api's public list): the average, how many
// gave each number of stars, and the reviews, newest first, with the coach's replies. Only buyers can
// review, so every review is from someone who owns the program.
export function ProgramReviews({ programId, coachName }: { programId: string; coachName: string }) {
  const { api } = useApp()
  const reviews = useInfiniteQuery({
    queryKey: ['reviews', programId],
    queryFn: async ({ pageParam }) =>
      (
        await api.GET('/api/v1/marketplace/programs/{programId}/reviews', {
          params: { path: { programId }, query: { Page: pageParam, PageSize } },
        })
      ).data ?? null,
    initialPageParam: 1,
    getNextPageParam: (last, pages) => (last && pages.length * PageSize < Number(last.totalCount) ? pages.length + 1 : undefined),
  })

  const first = reviews.data?.pages[0]
  // Absent while loading, or from an enrollment-api older than reviews (a rolling deploy).
  if (!first) return null
  const count = Number(first.count)
  const average = Number(first.average)
  const stars = first.stars.map(Number)
  const items = reviews.data?.pages.flatMap((p) => p?.items ?? []) ?? []

  return (
    <section id="reviews" aria-labelledby="reviews-heading" className="scroll-mt-28 space-y-6 rounded-3xl border bg-card p-6 sm:p-8">
      <h2 id="reviews-heading" className="text-xl font-bold">
        Reviews
      </h2>
      {count === 0 ? (
        <p className="text-sm text-muted-foreground">No reviews yet. Customers who buy this program can review it from their program page.</p>
      ) : (
        <>
          <div className="grid gap-6 sm:grid-cols-[auto_minmax(0,1fr)] sm:items-center sm:gap-10">
            <div className="space-y-1">
              <p className="font-heading text-5xl font-black tabular-nums">{formatRating(average)}</p>
              <Stars value={average} />
              <p className="text-sm text-muted-foreground">
                {count} {count === 1 ? 'review' : 'reviews'}
              </p>
            </div>
            <dl className="space-y-1.5">
              {[5, 4, 3, 2, 1].map((n) => {
                const share = count ? Math.round((stars[n - 1]! / count) * 100) : 0
                return (
                  <div key={n} className="flex items-center gap-3 text-sm">
                    <dt className="w-14 shrink-0 text-muted-foreground">
                      {n} {n === 1 ? 'star' : 'stars'}
                    </dt>
                    <dd className="flex flex-1 items-center gap-3">
                      <span className="h-2 flex-1 overflow-hidden rounded-full bg-muted" aria-hidden>
                        <span className="block h-full rounded-full bg-amber-400" style={{ width: `${share}%` }} />
                      </span>
                      <span className="w-10 text-right tabular-nums">{share}%</span>
                    </dd>
                  </div>
                )
              })}
            </dl>
          </div>
          <ul className="divide-y">
            {items.map((r) => (
              <li key={r.id} className="space-y-2 py-5 first:pt-0 last:pb-0">
                <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                  <Stars value={Number(r.rating)} className="size-3.5" />
                  <span className="text-sm font-semibold">{r.authorName}</span>
                  <span className="text-xs text-muted-foreground">{new Date(r.createdAt).toLocaleDateString()}</span>
                </div>
                {r.comment && <p className="text-sm whitespace-pre-line">{r.comment}</p>}
                {r.reply && (
                  <div className="ml-4 space-y-1 rounded-2xl bg-muted/60 p-4 text-sm">
                    <p className="flex items-center gap-1.5 font-semibold">
                      <MessageSquareReply className="size-4" aria-hidden /> {coachName} replied
                    </p>
                    <p className="whitespace-pre-line text-muted-foreground">{r.reply}</p>
                  </div>
                )}
              </li>
            ))}
          </ul>
          {reviews.hasNextPage && (
            <Button variant="outline" onClick={() => reviews.fetchNextPage()} disabled={reviews.isFetchingNextPage}>
              {reviews.isFetchingNextPage ? 'Loading…' : 'Show more reviews'}
            </Button>
          )}
        </>
      )}
    </section>
  )
}
