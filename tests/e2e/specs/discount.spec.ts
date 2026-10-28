import { expect, test } from '@playwright/test'
import { desktopOnly, signIn, skipWithoutTestUser } from '../signIn'

// A discount code in the cart (design §2.4): ordering-api prices it against the coaches of the cart's
// programs. A code that does not exist is refused with a reason and the cart stays at full price. With
// E2E_DISCOUNT_CODE, a live code of E2E_PROGRAM_PATH's coach, the code is applied and shown in the
// total, then removed again; nothing is bought.
const programPath = process.env.E2E_PROGRAM_PATH || undefined
const liveCode = process.env.E2E_DISCOUNT_CODE || undefined

skipWithoutTestUser()
test.skip(!programPath, 'E2E_PROGRAM_PATH is not set')

test('the cart refuses an unknown discount code and applies a live one', async ({ page }, testInfo) => {
  desktopOnly(testInfo.project.name)
  await signIn(page)

  await page.goto(programPath!)
  const addToCart = page.getByRole('button', { name: /Add to cart/ })
  const inCart = page.getByRole('link', { name: /In cart/ })
  await expect(addToCart.or(inCart)).toBeVisible()
  if (await addToCart.isVisible()) await addToCart.click()
  await expect(inCart).toBeVisible()

  await page.goto('/cart')
  const code = page.getByLabel('Discount code')
  await code.fill(`NOPE${Date.now().toString(36).toUpperCase()}`.slice(0, 20))
  await page.getByRole('button', { name: 'Apply' }).click()
  await expect(page.getByRole('alert').filter({ hasText: /code/i })).toBeVisible()
  await expect(page.getByText(/^Discount \(/)).toBeHidden()

  if (!liveCode) return
  await code.fill(liveCode)
  await page.getByRole('button', { name: 'Apply' }).click()
  const applied = liveCode.toUpperCase()
  await expect(page.getByText(`Discount (${applied})`)).toBeVisible()
  await page.getByRole('button', { name: `Remove the code ${applied}` }).click()
  await expect(page.getByText(`Discount (${applied})`)).toBeHidden()
})
