import { expect, test } from '@playwright/test'
import { expectAccessible } from '../axe'

// Accessibility and the not-found pages (design §8), signed out, with the demo coaches (design §15.2).
// The signed-in pages are checked in account.spec.ts and studio.spec.ts.

const pages = [
  { name: 'marketplace', path: '/' },
  { name: 'filtered catalog', path: '/?category=Running&sort=PriceLowToHigh' },
  { name: 'storefront', path: '/c/demo-forge' },
  { name: 'program page', path: '/c/demo-forge/programs/strong-start' },
  { name: 'cart', path: '/cart' },
  { name: 'sell page', path: '/sell' },
  { name: 'coach directory', path: '/coaches' },
  { name: 'terms', path: '/legal/terms' },
]

for (const { name, path } of pages) {
  test(`the ${name} has no serious accessibility problems`, async ({ page }, testInfo) => {
    await page.goto(path)
    await expectAccessible(page, testInfo, name)
  })
}

test('an unknown coach or program shows a not-found page', async ({ page }) => {
  await page.goto('/c/no-such-coach-e2e')
  await expect(page.getByRole('heading', { name: 'Coach not found' })).toBeVisible()

  await page.goto('/c/demo-forge/programs/no-such-program-e2e')
  await expect(page.getByRole('heading', { name: 'Program not found' })).toBeVisible()

  await page.goto('/no/such/page')
  await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()
})
