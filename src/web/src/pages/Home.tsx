import { Link } from 'react-router'

export function Home() {
  return (
    <section>
      <h1>Training programs from independent coaches</h1>
      <p>The marketplace opens in a later phase.</p>
      <p>
        <Link to="/studio/new">Are you a coach? Open your workspace</Link>
      </p>
    </section>
  )
}
