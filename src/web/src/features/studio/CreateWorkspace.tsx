import { useMutation } from '@tanstack/react-query'
import { Store, Users, Wallet } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useApp } from '@/lib/AppContext'
import { CoachTermsVersion } from '@/lib/legal'
import { useTitle } from '@/lib/useTitle'
import { slugFrom } from '@/lib/slug'

// Becoming a coach (design §5): the signed-in user creates a workspace and becomes its owner.
export function CreateWorkspace() {
  const { auth, api } = useApp()
  const navigate = useNavigate()
  useTitle('Open your workspace')
  const [displayName, setDisplayName] = useState('')
  const [slug, setSlug] = useState('')
  const [slugEdited, setSlugEdited] = useState(false)
  // The Terms for coaches are accepted here (design §5.4); the version shown is sent.
  const [accepted, setAccepted] = useState(false)

  const create = useMutation({
    mutationFn: async () => {
      const { data, error, response } = await api.POST('/api/v1/tenants', { body: { slug, displayName, termsVersion: CoachTermsVersion } })
      if (response.status === 409 && (error as { reason?: string } | undefined)?.reason === 'terms') {
        throw new Error('The Terms for coaches have changed. Reload the page to read and accept the new version.')
      }
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
    <div className="grid gap-10 lg:grid-cols-2 lg:items-center lg:gap-16">
      <div className="space-y-6">
        <p className="text-xs font-semibold tracking-[0.25em] text-muted-foreground uppercase">For coaches</p>
        <h1 className="text-4xl leading-[0.95] font-black tracking-tight uppercase sm:text-6xl" style={{ fontStretch: '80%' }}>
          Open your studio
        </h1>
        <p className="max-w-md text-lg text-muted-foreground">Sell your training programs on Athlos, in a storefront that looks like your brand.</p>
        <ul className="space-y-4">
          {benefits.map((b) => (
            <li key={b.title} className="flex gap-4">
              <span className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-volt text-volt-foreground">
                <b.icon className="size-5" aria-hidden />
              </span>
              <span>
                <span className="block font-semibold">{b.title}</span>
                <span className="text-sm text-muted-foreground">{b.text}</span>
              </span>
            </li>
          ))}
        </ul>
      </div>
      <Card className="w-full lg:max-w-lg lg:justify-self-end">
        <CardHeader>
          <CardTitle className="text-2xl">Your workspace</CardTitle>
          <CardDescription>A name for your storefront and its address on Athlos.</CardDescription>
        </CardHeader>
        <CardContent>
          {!auth?.account ? (
            <div className="space-y-4">
              <p className="text-sm text-muted-foreground">Sign in or create an account first.</p>
              {auth && (
                <Button onClick={() => auth.signIn('/studio/new')}>Sign in</Button>
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
                  <span className="shrink-0 text-sm text-muted-foreground">athlos/c/</span>
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
              <label className="flex items-start gap-3 text-sm">
                <input type="checkbox" required checked={accepted} onChange={(e) => setAccepted(e.target.checked)} className="mt-0.5 size-4 accent-primary" />
                <span>
                  I accept the{' '}
                  <Link to="/legal/coaches" target="_blank" className="font-medium underline underline-offset-4">
                    Terms for coaches
                  </Link>
                  .
                </span>
              </label>
              <Button type="submit" disabled={create.isPending || !accepted}>
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
    </div>
  )
}

const benefits = [
  { icon: Store, title: 'Your own storefront', text: 'Your name, logo and colors on your page and your programs.' },
  { icon: Wallet, title: 'Paid through Stripe', text: 'Customers pay once; the money goes to your Stripe account.' },
  { icon: Users, title: 'Bring your team', text: 'Invite staff by email to help build programs.' },
]
