import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { StorefrontBanner } from '@/components/brand'
import { Field } from '@/components/field'
import { Section } from '@/components/page'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'
import { StudioTeam } from './StudioTeam.tsx'
import { useStudio } from './useStudio.ts'

const DefaultColor = '#18181b'

// Workspace settings (design §8: "configure your dedicated workspace"): the name, logo and color the
// storefront shows, and the team. Owners change them; staff see them read-only.
export function StudioSettings() {
  const { slug, workspace, isOwner } = useStudio()
  const { api } = useApp()
  const queryClient = useQueryClient()
  const current = workspace.data
  const [color, setColor] = useState(current?.branding.primaryColor ?? DefaultColor)
  const [logo, setLogo] = useState(current?.branding.logoUrl ?? '')
  const [name, setName] = useState(current?.displayName ?? '')

  const save = useMutation({
    mutationFn: async () => {
      const { error, response } = await api.PUT('/api/v1/studio/{tenantSlug}/workspace', {
        params: { path: { tenantSlug: slug } },
        body: { displayName: name.trim(), logoUrl: logo.trim() || null, primaryColor: color },
      })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not save the settings.')
    },
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: ['workspace', slug] }),
        queryClient.invalidateQueries({ queryKey: ['storefront', slug] }),
        queryClient.invalidateQueries({ queryKey: ['memberships'] }),
      ]),
  })

  if (!current) return null

  return (
    <div className="space-y-12">
      <Section title="Storefront">
        <div className="grid gap-6 lg:grid-cols-2">
          <Card>
            <CardHeader>
              <CardTitle>Workspace</CardTitle>
              <CardDescription>
                {isOwner ? 'How your storefront looks to customers.' : 'Only the owner can change these.'}
              </CardDescription>
            </CardHeader>
            <CardContent>
              <form
                onSubmit={(e: FormEvent) => {
                  e.preventDefault()
                  save.mutate()
                }}
                className="space-y-4"
              >
                <fieldset disabled={!isOwner} className="space-y-4">
                  <Field id="ws-name" label="Name">
                    <Input id="ws-name" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
                  </Field>
                  <Field id="ws-logo" label="Logo URL (optional)" hint="A link to a square image, for example from your website.">
                    <Input id="ws-logo" type="url" maxLength={500} value={logo} onChange={(e) => setLogo(e.target.value)} />
                  </Field>
                  <Field id="ws-color" label="Brand color">
                    <div className="flex items-center gap-3">
                      <input
                        id="ws-color"
                        type="color"
                        value={color}
                        onChange={(e) => setColor(e.target.value)}
                        className="h-10 w-16 cursor-pointer rounded-lg border bg-transparent p-1"
                      />
                      <span className="font-mono text-sm text-muted-foreground">{color}</span>
                    </div>
                  </Field>
                </fieldset>
                {isOwner && (
                  <div className="flex items-center gap-4">
                    <Button type="submit" disabled={save.isPending}>
                      Save
                    </Button>
                    {save.isSuccess && !save.isPending && <span className="text-sm text-muted-foreground">Saved.</span>}
                  </div>
                )}
                {save.error && (
                  <Alert variant="destructive">
                    <AlertDescription>{save.error.message}</AlertDescription>
                  </Alert>
                )}
              </form>
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>Preview</CardTitle>
              <CardDescription>
                Your storefront at <span className="font-mono">/c/{slug}</span>
              </CardDescription>
            </CardHeader>
            <CardContent>
              <StorefrontBanner name={name} logoUrl={logo || null} color={color} heading="p" className="rounded-2xl" innerClassName="px-5 py-8" />
            </CardContent>
          </Card>
        </div>
      </Section>
      {isOwner && <StudioTeam slug={slug} />}
    </div>
  )
}
