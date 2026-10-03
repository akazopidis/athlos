import { Link } from 'react-router'
import { ProgramList } from './ProgramList.tsx'

// The marketplace (design §8, "/"). Search and filters come with the full UI in phase 6.
export function Home() {
  return (
    <section>
      <h1>Training programs from independent coaches</h1>
      <ProgramList />
      <p>
        <Link to="/studio/new">Are you a coach? Open your workspace</Link>
      </p>
    </section>
  )
}
