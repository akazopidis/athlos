import { useTitle } from '@/lib/useTitle'
import { Search } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { selectClassName } from '@/components/field'
import { cn } from '@/lib/utils'
import { PageHeader } from '@/components/page'
import { ProgramList } from './ProgramList.tsx'
import { Levels, useProgramSearch, type Level } from './useProgramSearch.ts'

// The marketplace (design §8, "/"): browse and search all coaches' published programs.
export function Home() {
  const { search, update } = useProgramSearch()
  useTitle('Programs')
  const [text, setText] = useState(search.q ?? '')

  // Search as the visitor types, once they pause.
  useEffect(() => {
    if (text === (search.q ?? '')) return
    const timer = setTimeout(() => update({ q: text }), 300)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- only the typed text starts a search
  }, [text])

  return (
    <>
      <PageHeader title="Training programs from independent coaches" description="Buy a program once, train at your own pace.">
        <Button asChild variant="outline">
          <Link to="/studio/new">Are you a coach? Open your workspace</Link>
        </Button>
      </PageHeader>
      <div className="mb-6 flex flex-wrap gap-3">
        <div className="relative min-w-64 flex-1">
          <Search className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
          <Input
            type="search"
            aria-label="Search programs"
            placeholder="Search programs"
            className="pl-9"
            value={text}
            onChange={(e) => setText(e.target.value)}
          />
        </div>
        <select
          aria-label="Level"
          className={cn(selectClassName, 'w-auto')}
          value={search.level ?? ''}
          onChange={(e) => update({ level: (e.target.value || null) as Level | null })}
        >
          <option value="">Any level</option>
          {Levels.map((l) => (
            <option key={l}>{l}</option>
          ))}
        </select>
      </div>
      <ProgramList search={search} onPage={(page) => update({ page })} />
    </>
  )
}
