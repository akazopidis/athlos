import { Info } from 'lucide-react'
import { Link, useParams, useSearchParams } from 'react-router'
import { BrandScope, StorefrontBanner } from '@/components/brand'
import { CoachLinks } from '@/components/coach-profile'
import { ReportContent } from '@/components/report-content'
import { Loading, Notice, Section } from '@/components/page'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useMyWorkspaces } from '@/lib/useMyWorkspaces'
import { useTitle } from '@/lib/useTitle'
import { Catalog } from './Catalog.tsx'
import { useProgramSearch } from './useProgramSearch.ts'
import { useStorefront } from './useStorefront.ts'

// The coach's public page, in their own colors (design §8): the visible "your own workspace" part
// of the thesis. Logo, color, bio and links come from the workspace settings (studio).
export function Storefront() {
  const { slug = '' } = useParams()
  const [params] = useSearchParams()
  const { search, update, clear } = useProgramSearch()
  const storefront = useStorefront(slug)
  const mine = useMyWorkspaces().has(slug)

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
        {/* Checkouts from before the cart send the customer back here when they leave Stripe's page. */}
        {params.get('checkout') === 'cancelled' && (
          <Alert>
            <Info aria-hidden />
            <AlertDescription>Checkout cancelled. You have not been charged.</AlertDescription>
          </Alert>
        )}
        {(branding.bio || branding.instagramUrl || branding.youTubeUrl || branding.websiteUrl || branding.contactEmail) && (
          <section aria-labelledby="about-heading" className="grid gap-4 rounded-3xl border bg-card p-6 sm:p-8 md:grid-cols-[minmax(0,2fr)_minmax(0,1fr)] md:items-start">
            <div className="space-y-3">
              <h2 id="about-heading" className="text-xl font-bold tracking-tight">
                About {displayName}
              </h2>
              {branding.bio && <p className="whitespace-pre-line text-muted-foreground">{branding.bio}</p>}
            </div>
            <CoachLinks name={displayName} profile={branding} className="md:justify-end" />
          </section>
        )}
        <Section title={`Programs by ${displayName}`}>
          <Catalog search={search} update={update} clear={clear} fixedCoach={slug} />
        </Section>
        {!mine && <ReportContent target={{ kind: 'Coach', coachSlug: slug }} label="Report this coach" />}
      </div>
    </BrandScope>
  )
}
