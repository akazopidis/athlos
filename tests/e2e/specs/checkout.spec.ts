import { expect, test } from '@playwright/test'
import { desktopOnly, signIn, skipWithoutTestUser } from '../signIn'

// The signed-in path (design §15.1): sign in through Entra External ID, put a program in the cart,
// check out, and arrive on Stripe's payment page with the right total. It stops there, without
// paying: the test can run after every deploy without buying the same program again and again, and
// the unpaid Stripe session is closed by the next checkout or expires.
//
// Needs, besides the test customer (signIn.ts), the GitHub environment variable E2E_PROGRAM_PATH: a
// program of a coach who finished Stripe onboarding (demo coaches cannot sell), for example
// /c/some-coach/programs/some-program. Without it the test is skipped.
const programPath = process.env.E2E_PROGRAM_PATH || undefined

skipWithoutTestUser()
test.skip(!programPath, 'E2E_PROGRAM_PATH is not set')

test('a signed-in customer checks out a cart and reaches Stripe', async ({ page }, testInfo) => {
  desktopOnly(testInfo.project.name)
  await signIn(page)

  await page.goto(programPath!)
  const price = (await page.locator('aside p.font-heading').first().innerText()).trim()
  // The buy box waits for the customer's workspaces and programs before it offers anything.
  const addToCart = page.getByRole('button', { name: /Add to cart/ })
  const inCart = page.getByRole('link', { name: /In cart/ })
  await expect(addToCart.or(inCart)).toBeVisible()
  if (await addToCart.isVisible()) await addToCart.click()
  await expect(inCart).toBeVisible()

  await page.goto('/cart')
  await page.getByRole('checkbox', { name: /I accept the Terms/ }).check()
  await page.getByRole('button', { name: 'Check out' }).click()

  await page.waitForURL(/checkout\.stripe\.com/, { timeout: 150_000 })
  // Stripe shows the total; the number matches the cart's (formats differ: "€19.00" / "19,00 €").
  const amount = price.replace(/[^\d.,]/g, '')
  await expect(page.getByText(new RegExp(amount.replace('.', '[.,]'))).first()).toBeVisible()
})
