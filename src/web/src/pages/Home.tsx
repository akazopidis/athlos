import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { PageHeader } from '@/components/page'
import { ProgramList } from './ProgramList.tsx'

// The marketplace (design §8, "/"): every coach's published programs. Search and filters come next.
export function Home() {
  return (
    <>
      <PageHeader title="Training programs from independent coaches" description="Buy a program once, train at your own pace.">
        <Button asChild variant="outline">
          <Link to="/studio/new">Are you a coach? Open your workspace</Link>
        </Button>
      </PageHeader>
      <ProgramList />
    </>
  )
}
