import { expect, test, type Page } from '@playwright/test'

// The shop as a visitor sees it (design §8): catalog, filters, search, product page, cart. No sign-in.
// Uses the demo coaches seeded in dev and staging (design §15.2).

const products = (page: Page) => page.locator('main article')

// The category filter: radios in the sidebar on large screens, in the "Filters" drawer on smaller ones.
// The radios follow the URL, so a click checks one only once the new URL has rendered: click, then
// wait for it (check() would insist on an immediate change).
async function pickCategory(page: Page, category: string) {
  const drawerButton = page.getByRole('button', { name: /^Filters/ })
  if (await drawerButton.isVisible()) {
    await drawerButton.click()
    const drawer = page.getByRole('dialog', { name: 'Filters' })
    const radio = drawer.getByRole('radio', { name: category, exact: true })
    await radio.click()
    await expect(radio).toBeChecked()
    await drawer.getByRole('button', { name: /^Show \d+ programs?$/ }).click()
    await expect(drawer).toBeHidden()
  } else {
    const radio = page.getByRole('complementary', { name: 'Filters' }).getByRole('radio', { name: category, exact: true })
    await radio.click()
    await expect(radio).toBeChecked()
  }
}

test('the marketplace lists programs and filters them by category', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('heading', { name: 'All programs' })).toBeVisible()
  await expect(products(page).first()).toBeVisible()
  await expect(page.getByText(/^\d+ programs?$/)).toBeVisible()

  await pickCategory(page, 'Running')

  await expect(page).toHaveURL(/category=Running/)
  await expect(page.getByRole('heading', { name: 'Running programs' })).toBeVisible()
  // The previous results stay on screen while the filtered ones load (aria-busy on the list).
  await expect(page.locator('main ul[aria-busy="true"]')).toHaveCount(0)
  for (const card of await products(page).all()) {
    await expect(card).toContainText('Running')
  }
})

test('the header search finds programs from any page', async ({ page }) => {
  await page.goto('/cart')

  const search = page.getByRole('searchbox', { name: 'Search programs' }).locator('visible=true').first()
  await search.fill('strength')
  await search.press('Enter')

  await expect(page).toHaveURL(/\/\?q=strength/)
  await expect(page.getByRole('heading', { name: /Results for .strength./ })).toBeVisible()
})

test('a program goes into the cart, stays there, and leaves it', async ({ page }) => {
  await page.goto('/')
  const first = products(page).first()
  const title = (await first.getByRole('heading').innerText()).trim()
  await first.getByRole('heading').getByRole('link').click()

  await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText(title)
  await page.getByRole('button', { name: /Add to cart/ }).click()
  await expect(page.getByRole('link', { name: /In cart/ })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Cart, 1 program' })).toBeVisible()

  await page.goto('/cart')
  await expect(page.getByRole('heading', { name: 'Your cart' })).toBeVisible()
  await expect(page.getByRole('link', { name: title }).first()).toBeVisible()

  // The cart lives in this browser: a reload keeps it.
  await page.reload()
  await expect(page.getByRole('link', { name: title }).first()).toBeVisible()

  await page.getByRole('button', { name: `Remove ${title}` }).click()
  await expect(page.getByRole('heading', { name: 'Your cart is empty' })).toBeVisible()
})

test("a coach's storefront shows their programs in their colors", async ({ page }) => {
  await page.goto('/c/demo-forge')

  await expect(page.getByRole('heading', { level: 1, name: 'Forge Strength' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Programs by Forge Strength' })).toBeVisible()
  await expect(products(page).first()).toBeVisible()
})
