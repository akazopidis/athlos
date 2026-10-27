import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Trash2 } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Field } from '@/components/field'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'

// Deleting the workspace (design §3.4): the owner types the address to confirm. Everything goes,
// including what customers bought; their orders stay for accounting.
export function DeleteWorkspace({ slug }: { slug: string }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [confirm, setConfirm] = useState('')

  const remove = useMutation({
    mutationFn: async () => {
      const { error, response } = await api.DELETE('/api/v1/studio/{tenantSlug}/workspace', {
        params: { path: { tenantSlug: slug } },
        body: { confirmSlug: confirm.trim() },
      })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not delete the workspace.')
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['memberships'] })
      navigate('/me/workspaces')
    },
  })

  const submit = (e: FormEvent) => {
    e.preventDefault()
    remove.mutate()
  }

  return (
    <Card className="border-destructive/40">
      <CardHeader>
        <CardTitle>Delete workspace</CardTitle>
        <CardDescription>
          Removes your storefront, programs, workouts, images and team for good. Customers keep a read-only copy of the programs they bought,
          with their progress; their orders are kept for accounting. Your Stripe account stays yours at Stripe.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={submit} className="flex flex-wrap items-end gap-3">
          <Field id="confirm-delete" label={<>Type <span className="font-mono">{slug}</span> to confirm</>} className="min-w-64 flex-1">
            <Input id="confirm-delete" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="off" />
          </Field>
          <Button type="submit" variant="destructive" disabled={confirm.trim() !== slug || remove.isPending}>
            <Trash2 aria-hidden /> Delete workspace
          </Button>
        </form>
        {remove.error && (
          <Alert variant="destructive" className="mt-4">
            <AlertDescription>{remove.error.message}</AlertDescription>
          </Alert>
        )}
      </CardContent>
    </Card>
  )
}
