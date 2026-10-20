import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'

// Accessibility and the not-found pages (design §8), signed out, with the demo coaches (design §15.2).
// axe checks each page against WCAG 2.1 A and AA; a serious or critical violation fails the test, and
// the full list is attached to the report.

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
    await expect(page.locator('main')).toBeVisible()
    // The catalog keeps showing earlier results while it loads (aria-busy); wait until it is done.
    await expect(page.locator('[aria-busy="true"]')).toHaveCount(0)

    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
    await testInfo.attach('axe', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' })
    const serious = results.violations
      .filter((v) => v.impact === 'serious' || v.impact === 'critical')
      .map((v) => `${v.id} (${v.impact}): ${v.help} — ${v.nodes.length} element(s), e.g. ${v.nodes[0]?.target.join(' ')}`)
    expect(serious).toEqual([])
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
