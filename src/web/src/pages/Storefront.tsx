import { useQuery } from '@tanstack/react-query'
import { useParams, useSearchParams } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Loading, Section } from '@/components/page'
import { useApp } from '../AppContext.tsx'
import { ProgramList } from './ProgramList.tsx'
import { useProgramSearch } from './useProgramSearch.ts'

// The coach's public page, in their own colors (design §8): the visible "your own workspace" part
// of the thesis. Logo and color come from the workspace settings (studio).
export function Storefront() {
  const { slug = '' } = useParams()
  const [params] = useSearchParams()
  const { search, update } = useProgramSearch()
  const { api } = useApp()
  const storefront = useQuery({
    queryKey: ['storefront', slug],
    queryFn: async () => (await api.GET('/api/v1/marketplace/coaches/{slug}', { params: { path: { slug } } })).data ?? null,
  })

  if (storefront.isPending) return <Loading />
  if (!storefront.data) return <p>Coach not found.</p>

  const { displayName, branding } = storefront.data
  const color = branding.primaryColor ?? undefined
  return (
    <div className="space-y-8">
      <div
        className="relative overflow-hidden rounded-xl border bg-muted px-6 py-10"
        style={color ? { backgroundColor: `color-mix(in oklab, ${color} 14%, transparent)`, borderColor: color } : undefined}
      >
        <div className="absolute inset-x-0 top-0 h-1.5" style={{ backgroundColor: color ?? 'var(--primary)' }} aria-hidden />
        <div className="flex items-center gap-5">
          {branding.logoUrl ? (
            <img src={branding.logoUrl} alt="" className="size-16 rounded-xl bg-background object-cover shadow-sm" />
          ) : (
            <div
              className="flex size-16 items-center justify-center rounded-xl text-2xl font-semibold text-white shadow-sm"
              style={{ backgroundColor: color ?? 'var(--primary)' }}
              aria-hidden
            >
              {displayName.slice(0, 1).toUpperCase()}
            </div>
          )}
          <div>
            <h1 className="text-3xl font-semibold tracking-tight">{displayName}</h1>
            <p className="text-sm text-muted-foreground">Coach on Athlos</p>
          </div>
        </div>
      </div>
      {/* Stripe sends the customer back here when they leave the checkout page. */}
      {params.get('checkout') === 'cancelled' && (
        <Alert>
          <AlertDescription>Checkout cancelled. You have not been charged.</AlertDescription>
        </Alert>
      )}
      <Section title="Programs">
        <ProgramList search={{ coach: slug, page: search.page }} onPage={(page) => update({ page })} />
      </Section>
    </div>
  )
}
