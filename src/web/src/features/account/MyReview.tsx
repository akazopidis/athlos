import { useMutation, useQueryClient } from '@tanstack/react-query'
import { MessageSquareReply, Pencil } from 'lucide-react'
import { useState } from 'react'
import type { components } from '@/api/enrollment'
import { Field } from '@/components/field'
import { StarInput, Stars } from '@/components/stars'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'

type Review = components['schemas']['MyReviewResponse']

const MaxComment = 2000

// The customer's review of a program they bought (design §2.4): stars and an optional comment, shown on
// the program's page under their first name and initial. They can change or delete it; the coach's
// reply appears here too. The parent remounts it (key) when the saved review changes.
export function MyReview({ programId, review }: { programId: string; review: Review | null | undefined }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState(!review)
  const [rating, setRating] = useState(review ? Number(review.rating) : 0)
  const [comment, setComment] = useState(review?.comment ?? '')

  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ['my-program', programId] }),
      queryClient.invalidateQueries({ queryKey: ['reviews', programId] }),
    ])

  const save = useMutation({
    mutationFn: async () => {
      const { error, response } = await api.PUT('/api/v1/me/programs/{programId}/review', {
        params: { path: { programId } },
        body: { rating, comment: comment.trim() || null },
      })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not save your review.')
    },
    onSuccess: refresh,
  })

  const remove = useMutation({
    mutationFn: async () => {
      const { error, response } = await api.DELETE('/api/v1/me/programs/{programId}/review', { params: { path: { programId } } })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not delete your review.')
    },
    onSuccess: refresh,
  })

  const error = save.error ?? remove.error

  return (
    <section aria-labelledby="my-review-heading" className="space-y-4 rounded-3xl border bg-card p-6 sm:p-8">
      <div className="space-y-1">
        <h2 id="my-review-heading" className="text-xl font-bold">
          {review ? 'Your review' : 'Review this program'}
        </h2>
        <p className="text-sm text-muted-foreground">Your review appears on the program's page in the shop, with your first name and initial.</p>
      </div>
      {editing ? (
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault()
            save.mutate()
          }}
        >
          <StarInput name="rating" label="Your rating" value={rating} onChange={setRating} />
          <Field id="review-comment" label="What did you think? (optional)" hint={`${comment.length}/${MaxComment}`}>
            <Textarea id="review-comment" value={comment} maxLength={MaxComment} rows={4} onChange={(e) => setComment(e.target.value)} />
          </Field>
          <div className="flex flex-wrap gap-2">
            <Button type="submit" disabled={rating === 0 || save.isPending}>
              {save.isPending ? 'Saving…' : review ? 'Save changes' : 'Post review'}
            </Button>
            {review && (
              <Button type="button" variant="ghost" onClick={() => setEditing(false)}>
                Cancel
              </Button>
            )}
          </div>
        </form>
      ) : (
        review && (
          <div className="space-y-3">
            <Stars value={Number(review.rating)} />
            {review.hiddenAt && (
              <p className="text-sm text-muted-foreground">
                Athlos hid this review after a report, so it no longer shows on the program's page. You can edit it or delete it.
              </p>
            )}
            {review.comment && <p className="text-sm whitespace-pre-line">{review.comment}</p>}
            {review.reply && (
              <div className="space-y-1 rounded-2xl bg-muted/60 p-4 text-sm">
                <p className="flex items-center gap-1.5 font-semibold">
                  <MessageSquareReply className="size-4" aria-hidden /> The coach replied
                </p>
                <p className="whitespace-pre-line text-muted-foreground">{review.reply}</p>
              </div>
            )}
            <div className="flex flex-wrap gap-2">
              <Button variant="outline" size="sm" onClick={() => setEditing(true)}>
                <Pencil aria-hidden /> Edit
              </Button>
              <Button variant="ghost" size="sm" onClick={() => remove.mutate()} disabled={remove.isPending}>
                {remove.isPending ? 'Deleting…' : 'Delete review'}
              </Button>
            </div>
          </div>
        )
      )}
      {error && (
        <Alert variant="destructive" role="alert">
          <AlertDescription>{error.message}</AlertDescription>
        </Alert>
      )}
    </section>
  )
}
