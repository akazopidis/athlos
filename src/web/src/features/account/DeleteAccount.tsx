import { useMutation } from '@tanstack/react-query'
import { Trash2 } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Field } from '@/components/field'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'

const Confirmation = 'delete my account'

// Deleting your account (design §5.2): your programs, progress and team memberships go, your orders
// stay with the coaches without your address, and your sign-in account is deleted. Owners delete
// their workspaces first; the server says which.
export function DeleteAccount() {
  const { api, msal } = useApp()
  const [confirm, setConfirm] = useState('')

  const remove = useMutation({
    mutationFn: async () => {
      const { error, response } = await api.DELETE('/api/v1/me')
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not delete your account. Try again.')
    },
    // The sign-in account no longer exists, so end the session here too.
    onSuccess: () => msal?.logoutRedirect(),
  })

  const submit = (e: FormEvent) => {
    e.preventDefault()
    remove.mutate()
  }

  return (
    <Card className="border-destructive/40">
      <CardHeader>
        <CardTitle>Delete account</CardTitle>
        <CardDescription>
          Removes your programs, progress and team memberships, and your sign-in account. Coaches keep your orders for their accounting, without your
          email address. If you own a workspace, delete it first.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={submit} className="flex flex-wrap items-end gap-3">
          <Field id="confirm-account-delete" label={<>Type <span className="font-mono">{Confirmation}</span> to confirm</>} className="min-w-64 flex-1">
            <Input id="confirm-account-delete" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="off" />
          </Field>
          <Button type="submit" variant="destructive" disabled={confirm.trim().toLowerCase() !== Confirmation || remove.isPending}>
            <Trash2 aria-hidden /> Delete account
          </Button>
        </form>
        {remove.error && (
          <Alert variant="destructive" className="mt-4" role="alert">
            <AlertDescription>{remove.error.message}</AlertDescription>
          </Alert>
        )}
      </CardContent>
    </Card>
  )
}
