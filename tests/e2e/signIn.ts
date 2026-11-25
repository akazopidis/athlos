import { expect, test, type Page } from '@playwright/test'

// The test customer (design §15.1), created by a person in the External ID tenant. Needs, as GitHub
// environment secrets of the stage the tests run against: E2E_USER_EMAIL, E2E_USER_PASSWORD. Without
// them the signed-in tests are skipped.
export const email = process.env.E2E_USER_EMAIL || undefined
export const password = process.env.E2E_USER_PASSWORD || undefined

export const skipWithoutTestUser = () => test.skip(!email || !password, 'E2E_USER_EMAIL and E2E_USER_PASSWORD are not set')

// One sign-in flow is enough; the layout is covered by the other specs at every size.
export const desktopOnly = (project: string) => test.skip(project !== 'desktop', 'desktop only')

// Signs in through Entra External ID's page (the "Athlos sign-up and sign-in" user flow): email, then
// password, then "Stay signed in?" when Entra asks it, and back to Athlos.
export async function signIn(page: Page) {
  await page.goto('/')
  await page.getByRole('button', { name: 'Sign in' }).click()

  await page.waitForURL(/ciamlogin\.com/)
  await page.getByRole('textbox', { name: /email/i }).fill(email!)
  await page.getByRole('button', { name: /next/i }).click()
  await page.getByLabel(/password/i).fill(password!)
  await page.getByRole('button', { name: /sign in/i }).click()
  // Entra then asks "Stay signed in?" (on the same /login address), or goes straight back to Athlos.
  const onEntra = (url: URL) => url.hostname === 'ciamlogin.com' || url.hostname.endsWith('.ciamlogin.com')
  const leftEntra = (url: URL) => !onEntra(url)
  const staySignedIn = page.getByRole('heading', { name: 'Stay signed in?' })
  await Promise.race([page.waitForURL(leftEntra), staySignedIn.waitFor()])
  if (await staySignedIn.isVisible()) await page.getByRole('button', { name: 'No', exact: true }).click()
  await page.waitForURL(leftEntra)
  await expect(page.getByRole('button', { name: 'Account' })).toBeVisible()
}
