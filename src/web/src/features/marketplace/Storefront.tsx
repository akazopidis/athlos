import { Info } from 'lucide-react'
import { Link, useParams, useSearchParams } from 'react-router'
import { BrandScope, StorefrontBanner } from '@/components/brand'
import { Loading, Notice, Section } from '@/components/page'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useTitle } from '@/lib/useTitle'
import { ProgramList } from './ProgramList.tsx'
import { useProgramSearch } from './useProgramSearch.ts'
import { useStorefront } from './useStorefront.ts'

// The coach's public page, in their own colors (design §8): the visible "your own workspace" part
// of the thesis. Logo and color come from the workspace settings (studio).
export function Storefront() {
  const { slug = '' } = useParams()
  const [params] = useSearchParams()
  const { search, update } = useProgramSearch()
  const storefront = useStorefront(slug)

  useTitle(storefront.data?.displayName)

  if (storefront.isPending) {
    return (
      <div className="container-page py-12">
        <Loading />
      </div>
    )
  }
  if (!storefront.data) {
    return (
      <Notice
        title="Coach not found"
        action={
          <Button asChild>
            <Link to="/">Browse all programs</Link>
          </Button>
        }
      >
        This storefront does not exist or is not open right now.
      </Notice>
    )
  }

  const { displayName, branding } = storefront.data
  return (
    <BrandScope color={branding.primaryColor}>
      <StorefrontBanner name={displayName} logoUrl={branding.logoUrl} color={branding.primaryColor} innerClassName="container-page py-12 sm:py-20" />
      <div className="container-page space-y-8 py-10 sm:py-14">
        {/* Stripe sends the customer back here when they leave the checkout page. */}
        {params.get('checkout') === 'cancelled' && (
          <Alert>
            <Info aria-hidden />
            <AlertDescription>Checkout cancelled. You have not been charged.</AlertDescription>
          </Alert>
        )}
        <Section title="Programs">
          <ProgramList search={{ coach: slug, page: search.page }} onPage={(page) => update({ page })} />
        </Section>
      </div>
    </BrandScope>
  )
}
