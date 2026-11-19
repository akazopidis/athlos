import { expect, test, type Frame, type Locator, type Page } from '@playwright/test'
import { desktopOnly, email, signIn, skipWithoutTestUser } from '../signIn'
import { canRefund, refundTestPurchases } from '../stripe'

// A whole purchase, then a review (design §2.2, §2.4), on staging in Stripe's test mode:
// 1. Buy now, pay on Stripe's page with Stripe's published test card (4242 4242 4242 4242), and come
//    back to "My programs", where the program appears once the webhook is handled.
// 2. Open the program, post a review, see it on the program's public page, and delete it again.
// 3. Refund the payment at Stripe with the stage's test key (E2E_STRIPE_SECRET_KEY) and wait until the
//    program page offers "Buy now" again: Stripe's charge.refunded event reached ordering-api, which
//    refunded the order, and enrollment removed the access. So every run pays again. Without the key
//    the customer keeps the program, and later runs skip straight to the review.
// The program is a separate one from E2E_PROGRAM_PATH (the checkout test needs one the
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

  if (!canRefund) {
    testInfo.annotations.push({ type: 'note', description: 'No Stripe test key: the customer keeps the program, so the next run will not pay.' })
    return
  }
  expect(await refundTestPurchases(email!, title)).toBeGreaterThan(0)
  await expect(async () => {
    await page.goto(programPath!)
    await expect(page.getByRole('button', { name: 'Buy now' })).toBeVisible({ timeout: 10_000 })
  }).toPass({ timeout: 180_000, intervals: [5_000] })
})

// A control on Stripe's hosted page, in whichever frame holds it (the page or one of its iframes),
// waiting up to `timeout` for the frames to load.
async function stripe(page: Page, locate: (frame: Frame) => Locator, timeout = 60_000): Promise<Locator> {
  const until = Date.now() + timeout
  for (;;) {
    for (const frame of page.frames()) {
      const control = locate(frame).first()
      if (await control.isVisible().catch(() => false)) return control
    }
    if (Date.now() > until) throw new Error(`Stripe's page has no such control after ${timeout} ms`)
    await page.waitForTimeout(500)
  }
}

// Buy now, then Stripe's hosted page in test mode, then back to Athlos until access exists.
async function pay(page: Page) {
  await page.getByRole('button', { name: 'Buy now' }).click()
  await page.waitForURL(/checkout\.stripe\.com/, { timeout: 150_000 })

  // Since November 2026 Stripe's page keeps the card fields in an iframe, found here by their names.
  await (await stripe(page, (f) => f.getByRole('textbox', { name: 'Card number' }))).fill('4242 4242 4242 4242')
  await (await stripe(page, (f) => f.getByRole('textbox', { name: /Expiration/ }))).fill('12 / 34')
  await (await stripe(page, (f) => f.getByRole('textbox', { name: /Security code|CVC/ }))).fill('123')
  await (await stripe(page, (f) => f.getByRole('textbox', { name: /Name on card|Cardholder/ }))).fill('Athlos E2E')
  const postalCode = await stripe(page, (f) => f.getByRole('textbox', { name: /ZIP|Postal/ }), 2_000).catch(() => null)
  if (postalCode && (await postalCode.isVisible())) await postalCode.fill('10001')
  // Stripe offers to save the details with Link, ticked, and then requires a phone number: untick it.
  const link = await stripe(page, (f) => f.getByRole('checkbox', { name: /Save my information/ }), 2_000).catch(() => null)
  if (link && (await link.isVisible()) && (await link.isChecked())) await link.uncheck()
  await (await stripe(page, (f) => f.getByRole('button', { name: 'Pay', exact: true }))).click()

  await page.waitForURL(/\/me\?checkout=success/, { timeout: 120_000 })
  // The program appears once Stripe's webhook is handled and enrollment-api enrolled the customer.
  await expect(page.getByRole('heading', { name: 'My programs' })).toBeVisible()
  await expect(page.getByText(/is confirmed/)).toBeVisible({ timeout: 120_000 })
}
