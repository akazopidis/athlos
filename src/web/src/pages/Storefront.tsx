import { useQuery } from '@tanstack/react-query'
import { useParams, useSearchParams } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Loading, Section } from '@/components/page'
import { useApp } from '../AppContext.tsx'
import { ProgramList } from './ProgramList.tsx'

// The coach's public page, in their own colors (design §8). Full branding comes with the studio settings.
export function Storefront() {
  const { slug = '' } = useParams()
  const [search] = useSearchParams()
  const { api } = useApp()
  const storefront = useQuery({
    queryKey: ['storefront', slug],
    queryFn: async () => (await api.GET('/api/v1/marketplace/coaches/{slug}', { params: { path: { slug } } })).data ?? null,
  })

  if (storefront.isPending) return <Loading />
  if (!storefront.data) return <p>Coach not found.</p>

  const { displayName, branding } = storefront.data
  return (
    <div className="space-y-8">
      <div className="flex items-center gap-4 border-t-4 pt-6" style={{ borderColor: branding.primaryColor ?? undefined }}>
        {branding.logoUrl && <img src={branding.logoUrl} alt="" className="size-14 rounded-lg object-cover" />}
        <h1 className="text-3xl font-semibold tracking-tight">{displayName}</h1>
      </div>
      {/* Stripe sends the customer back here when they leave the checkout page. */}
      {search.get('checkout') === 'cancelled' && (
        <Alert>
          <AlertDescription>Checkout cancelled. You have not been charged.</AlertDescription>
        </Alert>
      )}
      <Section title="Programs">
        <ProgramList coach={slug} />
      </Section>
    </div>
  )
}
