import { useMutation } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { useApp } from '../AppContext.tsx'
import { slugFrom } from '../slug.ts'

// Becoming a coach (design §5): the signed-in user creates a workspace and becomes its owner.
export function CreateWorkspace() {
  const { msal, config, api } = useApp()
  const navigate = useNavigate()
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

  if (!msal?.getActiveAccount()) {
    return (
      <section>
        <h1>Open your workspace</h1>
        <p>Sign in or create an account first.</p>
        {msal && (
          <button type="button" onClick={() => msal.loginRedirect({ scopes: [config.apiScope!], redirectStartPage: '/studio/new' })}>
            Sign in
          </button>
        )}
      </section>
    )
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    create.mutate()
  }

  return (
    <section>
      <h1>Open your workspace</h1>
      <form onSubmit={submit}>
        <label>
          Name
          <input
            required
            maxLength={100}
            value={displayName}
            onChange={(e) => {
              setDisplayName(e.target.value)
              if (!slugEdited) setSlug(slugFrom(e.target.value))
            }}
          />
        </label>
        <label>
          Address
          <span className="hint">athlos/c/</span>
          <input
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
        </label>
        <button type="submit" disabled={create.isPending}>
          Create workspace
        </button>
        {create.error && <p role="alert">{create.error.message}</p>}
      </form>
    </section>
  )
}
