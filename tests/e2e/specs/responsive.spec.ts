import { expect, test } from '@playwright/test'

// Responsive from phone to desktop (design §8): on every main page, at every size (the projects in
// playwright.config.ts), nothing is wider than the screen, so there is no sideways scrolling. A
// full-page screenshot of each is attached to the report, to look at.

const pages = [
  { name: 'marketplace', path: '/' },
  { name: 'storefront', path: '/c/demo-forge' },
  { name: 'cart', path: '/cart' },
  { name: 'filtered', path: '/?level=Intermediate&sort=PriceLowToHigh' },
]

for (const { name, path } of pages) {
  test(`${name} fits the screen`, async ({ page }, testInfo) => {
    await page.goto(path)
    await expect(page.locator('main')).toBeVisible()
    await page.waitForLoadState('networkidle')

    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)

    await testInfo.attach(`${name}-${testInfo.project.name}.png`, { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' })
    expect(overflow, `the page is ${overflow}px wider than the screen`).toBeLessThanOrEqual(1)
  })
}

test('a program page fits the screen', async ({ page }, testInfo) => {
  await page.goto('/c/demo-forge')
  await page.locator('main article').first().getByRole('heading').getByRole('link').click()
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toBeVisible()
  await page.waitForLoadState('networkidle')

  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)

  await testInfo.attach(`program-${testInfo.project.name}.png`, { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' })
  expect(overflow, `the page is ${overflow}px wider than the screen`).toBeLessThanOrEqual(1)
})
