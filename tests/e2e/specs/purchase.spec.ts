import { expect, test, type Page } from '@playwright/test'
import { desktopOnly, signIn, skipWithoutTestUser } from '../signIn'

// A whole purchase, then a review (design §2.2, §2.4), on staging in Stripe's test mode:
// 1. Buy now, pay on Stripe's page with Stripe's published test card (4242 4242 4242 4242), and come
//    back to "My programs", where the program appears once the webhook is handled.
// 2. Open the program, post a review, see it on the program's public page, and delete it again.
// Paying happens once per stage: the test customer then owns the program and later runs skip straight
// to the review. The program is a separate one from E2E_PROGRAM_PATH (the checkout test needs one the
// customer does not own): E2E_PURCHASE_PROGRAM_PATH, a program of a coach who finished Stripe
// onboarding, for example /c/some-coach/programs/some-other-program. Without it the test is skipped.
const programPath = process.env.E2E_PURCHASE_PROGRAM_PATH || undefined

skipWithoutTestUser()
test.skip(!programPath, 'E2E_PURCHASE_PROGRAM_PATH is not set')

test('a customer buys a program with a test card, then reviews it', async ({ page }, testInfo) => {
  desktopOnly(testInfo.project.name)
  test.setTimeout(6 * 60_000)
  await signIn(page)

  await page.goto(programPath!)
  const title = (await page.getByRole('heading', { level: 1 }).innerText()).trim()
  const open = page.getByRole('link', { name: 'Open program' })
  // The buy box waits for the customer's programs before it offers "Open" or "Buy now".
  await expect(open.or(page.getByRole('button', { name: 'Buy now' }))).toBeVisible()
  if (!(await open.isVisible())) await pay(page)

  // The review, from the customer's own program page.
  await page.goto(programPath!)
  await page.getByRole('link', { name: 'Open program' }).click()
  await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
  const deleteOld = page.getByRole('button', { name: 'Delete review' })
  if (await deleteOld.isVisible()) {
    await deleteOld.click()
    await expect(page.getByRole('button', { name: 'Post review' })).toBeVisible()
  }
  const comment = `E2E review ${Date.now().toString(36)}`
  await page.getByRole('radio', { name: '5 stars' }).check({ force: true })
  await page.getByLabel(/What did you think/).fill(comment)
  await page.getByRole('button', { name: 'Post review' }).click()
  await expect(page.getByRole('heading', { name: 'Your review' })).toBeVisible()

  await page.goto(programPath!)
  await expect(page.getByRole('region', { name: 'Reviews' }).getByText(comment)).toBeVisible()

  // Clean up, so the program's rating is not one test review forever.
  await page.goto(programPath!)
  await page.getByRole('link', { name: 'Open program' }).click()
  await page.getByRole('button', { name: 'Delete review' }).click()
  await expect(page.getByRole('button', { name: 'Post review' })).toBeVisible()
})

// Buy now, then Stripe's hosted page in test mode, then back to Athlos until access exists.
async function pay(page: Page) {
  await page.getByRole('button', { name: 'Buy now' }).click()
  await page.waitForURL(/checkout\.stripe\.com/, { timeout: 150_000 })

  await page.locator('#cardNumber').fill('4242 4242 4242 4242')
  await page.locator('#cardExpiry').fill('12 / 34')
  await page.locator('#cardCvc').fill('123')
  await page.locator('#billingName').fill('Athlos E2E')
  const postalCode = page.locator('#billingPostalCode')
  if (await postalCode.isVisible()) await postalCode.fill('10001')
  await page.locator('button[type="submit"]').click()

  await page.waitForURL(/\/me\?checkout=success/, { timeout: 120_000 })
  // The program appears once Stripe's webhook is handled and enrollment-api enrolled the customer.
  await expect(page.getByRole('heading', { name: 'My programs' })).toBeVisible()
  await expect(page.getByText(/is confirmed/)).toBeVisible({ timeout: 120_000 })
}
