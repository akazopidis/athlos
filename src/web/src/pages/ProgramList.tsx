import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { useApp } from '../AppContext.tsx'
import { formatMoney } from '../money.ts'

// Published programs, newest first: every coach's on the marketplace, one coach's on a storefront.
export function ProgramList({ coach }: { coach?: string }) {
  const { api } = useApp()
  const programs = useQuery({
    queryKey: ['programs', coach ?? null],
    queryFn: async () => (await api.GET('/api/v1/marketplace/programs', { params: { query: { Coach: coach } } })).data ?? null,
  })

  if (programs.isPending) return <p>Loading programs…</p>
  if (!programs.data?.items.length) return <p>No programs yet.</p>

  return (
    <ul className="programs">
      {programs.data.items.map((p) => (
        <li key={`${p.coachSlug}/${p.slug}`}>
          <Link to={`/c/${p.coachSlug}/programs/${p.slug}`}>
            {p.coverImageUrl && <img src={p.coverImageUrl} alt="" />}
            <strong>{p.title}</strong>
          </Link>
          <span>
            {p.level} · {p.durationWeeks} weeks · {formatMoney(p.priceAmount, p.currency)}
          </span>
        </li>
      ))}
    </ul>
  )
}
