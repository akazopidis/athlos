import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router'
import { useApp } from '../AppContext.tsx'

// The coach's public page, in their own colors (design §8).
export function Storefront() {
  const { slug = '' } = useParams()
  const { api } = useApp()
  const storefront = useQuery({
    queryKey: ['storefront', slug],
    queryFn: async () => (await api.GET('/api/v1/marketplace/coaches/{slug}', { params: { path: { slug } } })).data ?? null,
  })

  if (storefront.isPending) return <p>Loading…</p>
  if (!storefront.data) return <p>Coach not found.</p>

  const { displayName, branding } = storefront.data
  return (
    <section className="storefront" style={{ borderColor: branding.primaryColor ?? undefined }}>
      {branding.logoUrl && <img src={branding.logoUrl} alt="" height={48} />}
      <h1>{displayName}</h1>
    </section>
  )
}
