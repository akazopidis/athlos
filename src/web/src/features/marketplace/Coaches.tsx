import { Users } from 'lucide-react'
import { Link } from 'react-router'
import { CardGridSkeleton, Empty, PageHeader } from '@/components/page'
import { Button } from '@/components/ui/button'
import { useTitle } from '@/lib/useTitle'
import { CoachGrid } from './CoachGrid.tsx'
import { useMarketplaceCoaches } from './useMarketplaceCoaches.ts'

// "/coaches": every coach with programs on sale.
export function Coaches() {
  useTitle('Coaches')
  const { coaches, isPending } = useMarketplaceCoaches()

  return (
    <div className="space-y-8">
      <PageHeader eyebrow="Coaches" title="Train with independent coaches" description="Each coach sells their own programs, in their own storefront." />
      {isPending ? (
        <CardGridSkeleton count={4} />
      ) : coaches.length ? (
        <CoachGrid coaches={coaches} />
      ) : (
        <Empty
          icon={<Users />}
          title="No coaches yet"
          action={
            <Button asChild>
              <Link to="/sell">Be the first coach</Link>
            </Button>
          }
        >
          Coaches appear here once they publish a program.
        </Empty>
      )}
    </div>
  )
}
