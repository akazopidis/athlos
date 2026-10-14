import { expect, test } from '@playwright/test'

// The signed-in path (design §15.1): sign in through Entra External ID, put a program in the cart,
// check out, and arrive on Stripe's payment page with the right total. It stops there, without
// paying: the test can run after every deploy without buying the same program again and again, and
// the unpaid Stripe session is closed by the next checkout or expires.
//
// Needs, as GitHub environment secrets/variables of the stage it runs against:
//   E2E_USER_EMAIL, E2E_USER_PASSWORD: a test customer created by a person in the External ID tenant
//   E2E_PROGRAM_PATH: a program of a coach who finished Stripe onboarding (demo coaches cannot sell),
//                     for example /c/some-coach/programs/some-program
// Without them the test is skipped.
const email = process.env.E2E_USER_EMAIL || undefined
const password = process.env.E2E_USER_PASSWORD || undefined
const programPath = process.env.E2E_PROGRAM_PATH || undefined

test.skip(!email || !password || !programPath, 'E2E_USER_EMAIL, E2E_USER_PASSWORD and E2E_PROGRAM_PATH are not set')

test('a signed-in customer checks out a cart and reaches Stripe', async ({ page }, testInfo) => {
  // One sign-in flow is enough; the layout is covered by the other specs at every size.
  test.skip(testInfo.project.name !== 'desktop', 'desktop only')

  await page.goto('/')
  await page.getByRole('button', { name: 'Sign in' }).click()

  // Entra External ID's sign-in page (the "Athlos sign-up and sign-in" user flow): email, then password.
  await page.waitForURL(/ciamlogin\.com/)
  await page.getByRole('textbox', { name: /email/i }).fill(email!)
  await page.getByRole('button', { name: /next/i }).click()
  await page.getByLabel(/password/i).fill(password!)
  await page.getByRole('button', { name: /sign in/i }).click()
  // "Stay signed in?" appears for some accounts.
  const staySignedIn = page.getByRole('button', { name: /^(no|yes)$/i })
  await page.waitForURL((url) => !url.hostname.includes('ciamlogin.com') || url.pathname.includes('kmsi'))
  if (await staySignedIn.first().isVisible().catch(() => false)) await staySignedIn.first().click()
  await expect(page.getByRole('button', { name: 'Account' })).toBeVisible()

  await page.goto(programPath!)
  const price = (await page.locator('aside p.font-heading').first().innerText()).trim()
  const addToCart = page.getByRole('button', { name: /Add to cart/ })
  if (await addToCart.isVisible()) await addToCart.click()
  await expect(page.getByRole('link', { name: /In cart/ })).toBeVisible()

  await page.goto('/cart')
  await page.getByRole('button', { name: 'Check out' }).click()

  await page.waitForURL(/checkout\.stripe\.com/, { timeout: 150_000 })
  // Stripe shows the total; the number matches the cart's (formats differ: "€19.00" / "19,00 €").
  const amount = price.replace(/[^\d.,]/g, '')
  await expect(page.getByText(new RegExp(amount.replace('.', '[.,]'))).first()).toBeVisible()
})
