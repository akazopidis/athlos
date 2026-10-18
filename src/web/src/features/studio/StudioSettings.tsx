import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { StorefrontBanner } from '@/components/brand'
import { Field } from '@/components/field'
import { Section } from '@/components/page'
import { useApp } from '@/lib/AppContext'
import { problemTitle } from '@/lib/problem'
import { DeleteWorkspace } from './DeleteWorkspace.tsx'
import { StudioTeam } from './StudioTeam.tsx'
import { useStudio } from './useStudio.ts'

const DefaultColor = '#18181b'

// Workspace settings (design §8: "configure your dedicated workspace"): the name, logo, color and
// profile (bio, links, contact) the storefront shows, and the team. Owners change them; staff see them
// read-only.
export function StudioSettings() {
  const { slug, workspace, isOwner } = useStudio()
  const { api } = useApp()
  const queryClient = useQueryClient()
  const current = workspace.data
  const [color, setColor] = useState(current?.branding.primaryColor ?? DefaultColor)
  const [logo, setLogo] = useState(current?.branding.logoUrl ?? '')
  const [contact, setContact] = useState(current?.branding.contactEmail ?? '')
  const [bio, setBio] = useState(current?.branding.bio ?? '')
  const [instagram, setInstagram] = useState(current?.branding.instagramUrl ?? '')
  const [youTube, setYouTube] = useState(current?.branding.youTubeUrl ?? '')
  const [website, setWebsite] = useState(current?.branding.websiteUrl ?? '')
  const [name, setName] = useState(current?.displayName ?? '')

  const save = useMutation({
    mutationFn: async () => {
      const { error, response } = await api.PUT('/api/v1/studio/{tenantSlug}/workspace', {
        params: { path: { tenantSlug: slug } },
        body: {
          displayName: name.trim(),
          logoUrl: logo.trim() || null,
          primaryColor: color,
          contactEmail: contact.trim() || null,
          bio: bio.trim() || null,
          instagramUrl: instagram.trim() || null,
          youTubeUrl: youTube.trim() || null,
          websiteUrl: website.trim() || null,
        },
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
                  <Field
                    id="ws-contact"
                    label="Contact email (optional)"
                    hint="Shown on your storefront and to your customers in their orders, so they can reach you about a purchase or a refund."
                  >
                    <Input id="ws-contact" type="email" maxLength={254} value={contact} onChange={(e) => setContact(e.target.value)} />
                  </Field>
                  <Field id="ws-bio" label="About you (optional)" hint="A few lines on your storefront: who you coach, your background, how you train.">
                    <Textarea id="ws-bio" rows={4} maxLength={1500} value={bio} onChange={(e) => setBio(e.target.value)} />
                  </Field>
                  <div className="grid gap-4 sm:grid-cols-3">
                    <Field id="ws-instagram" label="Instagram">
                      <Input id="ws-instagram" type="url" maxLength={300} placeholder="https://instagram.com/…" value={instagram} onChange={(e) => setInstagram(e.target.value)} />
                    </Field>
                    <Field id="ws-youtube" label="YouTube">
                      <Input id="ws-youtube" type="url" maxLength={300} placeholder="https://youtube.com/@…" value={youTube} onChange={(e) => setYouTube(e.target.value)} />
                    </Field>
                    <Field id="ws-website" label="Website">
                      <Input id="ws-website" type="url" maxLength={300} placeholder="https://…" value={website} onChange={(e) => setWebsite(e.target.value)} />
                    </Field>
                  </div>
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
              <StorefrontBanner name={name} logoUrl={current.branding.uploadedLogoUrl ?? (logo || null)} color={color} heading="p" className="rounded-2xl" innerClassName="px-5 py-8" />
            </CardContent>
          </Card>
        </div>
      </Section>
      <StudioTeam slug={slug} isOwner={isOwner} />
      {isOwner && <LogoUpload slug={slug} uploadedLogoUrl={current.branding.uploadedLogoUrl ?? null} />}
      {isOwner && <DeleteWorkspace slug={slug} />}
    </div>
  )
}

// The logo or photo, uploaded (JPEG, PNG or WebP, up to 2 MB). It replaces a linked logo on the
// storefront; removing it brings the linked one back.
function LogoUpload({ slug, uploadedLogoUrl }: { slug: string; uploadedLogoUrl: string | null }) {
  const { api } = useApp()
  const queryClient = useQueryClient()
  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ['workspace', slug] }),
      queryClient.invalidateQueries({ queryKey: ['storefront', slug] }),
      queryClient.invalidateQueries({ queryKey: ['coaches'] }),
    ])

  const upload = useMutation({
    mutationFn: async (file: File) => {
      const { error, response } = await api.PUT('/api/v1/studio/{tenantSlug}/workspace/logo', {
        params: { path: { tenantSlug: slug } },
        body: file as unknown as never,
        bodySerializer: (body: unknown) => body as BodyInit,
        headers: { 'Content-Type': file.type || 'application/octet-stream' },
      })
      if (!response.ok) throw new Error(problemTitle(error) ?? 'Could not upload the image.')
    },
    onSuccess: refresh,
  })

  const remove = useMutation({
    mutationFn: async () => {
      const { response } = await api.DELETE('/api/v1/studio/{tenantSlug}/workspace/logo', { params: { path: { tenantSlug: slug } } })
      if (!response.ok) throw new Error('Could not remove the image.')
    },
    onSuccess: refresh,
  })

  return (
    <Section title="Logo or photo">
      <Card>
        <CardContent className="flex flex-wrap items-center gap-5">
          {uploadedLogoUrl ? (
            <img src={uploadedLogoUrl} alt="Your logo" className="size-20 rounded-2xl border object-cover" />
          ) : (
            <div className="flex size-20 items-center justify-center rounded-2xl border border-dashed text-xs text-muted-foreground">No image</div>
          )}
          <div className="min-w-0 flex-1 basis-56 space-y-2">
            <p className="text-sm text-muted-foreground">A square image works best: your logo or a photo of you. JPEG, PNG or WebP, up to 2 MB.</p>
            <div className="flex flex-wrap gap-2">
              <Button asChild size="sm" disabled={upload.isPending}>
                <label className="cursor-pointer">
                  {upload.isPending ? 'Uploading…' : uploadedLogoUrl ? 'Replace image' : 'Upload image'}
                  <input
                    type="file"
                    accept="image/jpeg,image/png,image/webp"
                    className="sr-only"
                    onChange={(e) => {
                      const file = e.target.files?.[0]
                      if (file) upload.mutate(file)
                      e.target.value = ''
                    }}
                  />
                </label>
              </Button>
              {uploadedLogoUrl && (
                <Button size="sm" variant="ghost" onClick={() => remove.mutate()} disabled={remove.isPending}>
                  Remove
                </Button>
              )}
            </div>
            {(upload.error ?? remove.error) && <p className="text-sm text-destructive" role="alert">{(upload.error ?? remove.error)!.message}</p>}
          </div>
        </CardContent>
      </Card>
    </Section>
  )
}
