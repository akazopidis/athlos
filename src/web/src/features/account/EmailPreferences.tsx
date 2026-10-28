import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Checkbox } from '@/components/ui/checkbox'
import { Skeleton } from '@/components/ui/skeleton'
import { useApp } from '@/lib/AppContext'
import type { components } from '@/api/enrollment'

type Preferences = components['schemas']['EmailPreferenceEndpointsPreferences']

const choices: { key: keyof Preferences; label: string; hint: string }[] = [
  { key: 'programUpdates', label: 'New workouts', hint: 'When a coach adds a workout to a program you own (at most once a day).' },
  { key: 'reviewReplies', label: 'Replies to your reviews', hint: 'When a coach replies to a review you wrote.' },
]

// The optional emails (design §5.3): each toggle saves at once. Purchase, refund and account emails
// always arrive, so they are not listed. Every optional email also has its own unsubscribe link.
export function EmailPreferences() {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const preferences = useQuery({
    queryKey: ['email-preferences'],
    queryFn: async () => {
      const { data } = await api.GET('/api/v1/me/email-preferences')
      if (!data) throw new Error('Your email choices could not be loaded.')
      return data
    },
  })
  const save = useMutation({
    mutationFn: async (next: Preferences) => {
      const { data } = await api.PUT('/api/v1/me/email-preferences', { body: next })
      if (!data) throw new Error('Your choice was not saved. Try again in a moment.')
      return data
    },
    onSuccess: (data) => queryClient.setQueryData(['email-preferences'], data),
  })
  const current = save.isPending ? save.variables : preferences.data

  return (
    <div className="space-y-3 rounded-2xl border bg-card p-4">
      <div>
        <p className="font-semibold">Emails</p>
        <p className="text-sm text-muted-foreground">Receipts, refunds and messages about your account always arrive. Choose the others:</p>
      </div>
      {!current ? (
        preferences.error ? (
          <p className="text-sm text-muted-foreground">{preferences.error.message}</p>
        ) : (
          <Skeleton className="h-16 w-full" />
        )
      ) : (
        <ul className="space-y-3">
          {choices.map((choice) => (
            <li key={choice.key} className="flex items-start gap-3">
              <Checkbox
                id={`email-${choice.key}`}
                className="mt-0.5"
                checked={current[choice.key]}
                disabled={save.isPending}
                onCheckedChange={(checked) => save.mutate({ ...current, [choice.key]: checked === true })}
              />
              <label htmlFor={`email-${choice.key}`} className="cursor-pointer text-sm">
                <span className="block font-medium">{choice.label}</span>
                <span className="block text-muted-foreground">{choice.hint}</span>
              </label>
            </li>
          ))}
        </ul>
      )}
      {save.error && (
        <Alert variant="destructive" role="alert">
          <AlertDescription>{save.error.message}</AlertDescription>
        </Alert>
      )}
    </div>
  )
}
