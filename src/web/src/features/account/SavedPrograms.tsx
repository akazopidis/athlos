import { Heart } from 'lucide-react'
import { Link } from 'react-router'
import { CardGridSkeleton, Empty, Section } from '@/components/page'
import { ProductCard } from '@/components/product-card'
import { Button } from '@/components/ui/button'
import { useCoaches } from '@/lib/useCoaches'
import { useMyWorkspaces } from '@/lib/useMyWorkspaces'
import { useOwnedPrograms } from '@/lib/useOwnedPrograms'
import { useSavedPrograms } from '@/lib/useSavedPrograms'
import { useTitle } from '@/lib/useTitle'

// The programs the customer saved for later (design §2.4, "/me/saved"), as shop cards. Only what is
// still on sale is listed; a program bought since shows as owned.
export function SavedPrograms() {
  useTitle('Saved programs')
  const saved = useSavedPrograms()
  const owned = useOwnedPrograms()
  const mine = useMyWorkspaces()
  const coaches = useCoaches([...new Set(saved.programs?.map((p) => p.coachSlug) ?? [])])

  return (
    <Section title="Saved programs">
      {saved.isPending ? (
        <CardGridSkeleton count={3} />
      ) : saved.programs?.length ? (
        <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {saved.programs.map((p) => (
            <li key={p.id}>
              <ProductCard program={p} coach={coaches.get(p.coachSlug)} owned={owned.has(p.id)} mine={mine.has(p.coachSlug)} />
            </li>
          ))}
        </ul>
      ) : (
        <Empty
          icon={<Heart />}
          title="Nothing saved yet"
          action={
            <Button asChild>
              <Link to="/">Browse programs</Link>
            </Button>
          }
        >
          Tap the heart on a program to keep it here for later.
        </Empty>
      )}
    </Section>
  )
}
