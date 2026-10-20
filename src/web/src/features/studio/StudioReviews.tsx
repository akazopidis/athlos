import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { MessageSquareReply, Star } from 'lucide-react'
import { useState } from 'react'
import type { components } from '@/api/enrollment'
import { Empty, Loading } from '@/components/page'
import { ReportReview } from '@/components/report-review'
import { Stars } from '@/components/stars'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'
import { useStudio } from './useStudio.ts'

type Review = components['schemas']['StudioReviewsItem']

const PageSize = 20

// The workspace's reviews (design §2.4, enrollment-api), newest first. Owners and staff answer each
// one with a public reply, shown under the review on the program's page; they can change or remove
// it. A customer's review itself is theirs: the coach cannot delete it.
export function StudioReviews() {
  const { slug } = useStudio()
  const { api } = useApp()
  const [page, setPage] = useState(1)

  const reviews = useQuery({
    queryKey: ['studio-reviews', slug, page],
    queryFn: async () =>
      (await api.GET('/api/v1/studio/{tenantSlug}/reviews', { params: { path: { tenantSlug: slug }, query: { Page: page, PageSize } } })).data ?? null,
    placeholderData: keepPreviousData,
  })

  if (reviews.isPending) return <Loading />
  if (!reviews.data?.items.length) {
    return (
      <Empty icon={<Star />} title="No reviews yet">
        Customers who buy your programs can review them. You get an email for each new review.
      </Empty>
    )
  }

  const pages = Math.max(1, Math.ceil(Number(reviews.data.totalCount) / PageSize))
  return (
    <div className="space-y-4">
      <ul className="space-y-4">
        {reviews.data.items.map((r) => (
          <ReviewCard key={`${r.id}-${r.repliedAt ?? ''}`} review={r} slug={slug} />
        ))}
      </ul>
      {pages > 1 && (
        <div className="flex items-center justify-between gap-4">
          <Button variant="outline" size="sm" disabled={page === 1} onClick={() => setPage(page - 1)}>
            Previous
          </Button>
          <span className="text-sm text-muted-foreground">
            Page {page} of {pages}
          </span>
          <Button variant="outline" size="sm" disabled={page >= pages} onClick={() => setPage(page + 1)}>
            Next
          </Button>
        </div>
      )}
    </div>
  )
}

function ReviewCard({ review: r, slug }: { review: Review; slug: string }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const [replying, setReplying] = useState(false)
  const [text, setText] = useState(r.reply ?? '')
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['studio-reviews', slug] })
  const path = { params: { path: { tenantSlug: slug, reviewId: r.id } } }

  const reply = useMutation({
    mutationFn: async () => {
      const { error, response } = await api.PUT('/api/v1/studio/{tenantSlug}/reviews/{reviewId}/reply', { ...path, body: { text } })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not save your reply.')
    },
    onSuccess: refresh,
  })
  const remove = useMutation({
    mutationFn: async () => {
      const { response } = await api.DELETE('/api/v1/studio/{tenantSlug}/reviews/{reviewId}/reply', path)
      if (!response.ok) throw new Error('Could not remove your reply.')
    },
    onSuccess: refresh,
  })
  const error = reply.error ?? remove.error

  return (
    <li className="space-y-3 rounded-2xl border bg-card p-5">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <Stars value={Number(r.rating)} />
        <span className="text-sm font-semibold">{r.authorName}</span>
        <span className="text-xs text-muted-foreground">{new Date(r.createdAt).toLocaleDateString()}</span>
        <span className="ml-auto truncate text-sm text-muted-foreground">{r.programTitle}</span>
      </div>
      {r.hiddenAt ? (
        <p className="text-xs font-medium text-muted-foreground">Hidden by Athlos after a report: it no longer shows on your program's page or in its rating.</p>
      ) : (
        <ReportReview programId={r.programId} reviewId={r.id} />
      )}
      {r.comment ? <p className="text-sm whitespace-pre-line">{r.comment}</p> : <p className="text-sm text-muted-foreground italic">No comment, just stars.</p>}
      {replying ? (
        <form
          className="space-y-2"
          onSubmit={(e) => {
            e.preventDefault()
            reply.mutate()
          }}
        >
          <label htmlFor={`reply-${r.id}`} className="text-sm font-medium">
            Your public reply
          </label>
          <Textarea id={`reply-${r.id}`} value={text} maxLength={2000} rows={3} onChange={(e) => setText(e.target.value)} />
          <div className="flex gap-2">
            <Button type="submit" size="sm" disabled={!text.trim() || reply.isPending}>
              {reply.isPending ? 'Saving…' : 'Post reply'}
            </Button>
            <Button type="button" size="sm" variant="ghost" onClick={() => setReplying(false)}>
              Cancel
            </Button>
          </div>
        </form>
      ) : r.reply ? (
        <div className="space-y-2 rounded-2xl bg-muted/60 p-4 text-sm">
          <p className="flex items-center gap-1.5 font-semibold">
            <MessageSquareReply className="size-4" aria-hidden /> Your reply
          </p>
          <p className="whitespace-pre-line text-muted-foreground">{r.reply}</p>
          <div className="flex gap-2">
            <Button size="sm" variant="outline" onClick={() => setReplying(true)}>
              Edit reply
            </Button>
            <Button size="sm" variant="ghost" onClick={() => remove.mutate()} disabled={remove.isPending}>
              Remove
            </Button>
          </div>
        </div>
      ) : (
        <Button size="sm" variant="outline" onClick={() => setReplying(true)}>
          <MessageSquareReply aria-hidden /> Reply
        </Button>
      )}
      {error && (
        <Alert variant="destructive" role="alert">
          <AlertDescription>{error.message}</AlertDescription>
        </Alert>
      )}
    </li>
  )
}
