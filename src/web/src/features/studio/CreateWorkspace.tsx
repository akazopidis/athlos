import { useMutation } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useApp } from '@/lib/AppContext'
import { useTitle } from '@/lib/useTitle'
import { slugFrom } from '@/lib/slug'

// Becoming a coach (design §5): the signed-in user creates a workspace and becomes its owner.
export function CreateWorkspace() {
  const { msal, config, api } = useApp()
  const navigate = useNavigate()
  useTitle('Open your workspace')
  const [displayName, setDisplayName] = useState('')
  const [slug, setSlug] = useState('')
  const [slugEdited, setSlugEdited] = useState(false)

  const create = useMutation({
    mutationFn: async () => {
      const { data, error, response } = await api.POST('/api/v1/tenants', { body: { slug, displayName } })
      if (response.status === 409) throw new Error('That address is taken. Try another one.')
      if (error || !data) throw new Error('Could not create the workspace.')
      return data
    },
    onSuccess: (created) => navigate(`/studio/${created.slug}`),
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    create.mutate()
  }

  return (
    <Card className="mx-auto max-w-lg">
      <CardHeader>
        <CardTitle className="text-2xl">Open your workspace</CardTitle>
        <CardDescription>Sell your training programs on Athlos. Payments go straight to your Stripe account.</CardDescription>
      </CardHeader>
      <CardContent>
        {!msal?.getActiveAccount() ? (
          <div className="space-y-4">
            <p className="text-sm text-muted-foreground">Sign in or create an account first.</p>
            {msal && (
              <Button onClick={() => msal.loginRedirect({ scopes: [config.apiScope!], redirectStartPage: '/studio/new' })}>Sign in</Button>
            )}
          </div>
        ) : (
          <form onSubmit={submit} className="space-y-5">
            <div className="space-y-2">
              <Label htmlFor="displayName">Name</Label>
              <Input
                id="displayName"
                required
                maxLength={100}
                value={displayName}
                onChange={(e) => {
                  setDisplayName(e.target.value)
                  if (!slugEdited) setSlug(slugFrom(e.target.value))
                }}
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="slug">Address</Label>
              <div className="flex items-center gap-2">
                <span className="text-sm text-muted-foreground">athlos/c/</span>
                <Input
                  id="slug"
                  required
                  minLength={3}
                  maxLength={40}
                  pattern="[a-z0-9]+(-[a-z0-9]+)*"
                  value={slug}
                  onChange={(e) => {
                    setSlugEdited(true)
                    setSlug(e.target.value)
                  }}
                />
              </div>
              <p className="text-xs text-muted-foreground">Lowercase letters, digits and dashes. It can't be changed later.</p>
            </div>
            <Button type="submit" disabled={create.isPending}>
              Create workspace
            </Button>
            {create.error && (
              <Alert variant="destructive">
                <AlertDescription>{create.error.message}</AlertDescription>
              </Alert>
            )}
          </form>
        )}
      </CardContent>
    </Card>
  )
}
