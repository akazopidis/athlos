import { expect, test } from '@playwright/test'
import { desktopOnly, signIn, skipWithoutTestUser } from '../signIn'

// The customer's account pages (design §8): the account menu opens each one directly, and the tabs
// move between them. The test customer's data differs per stage, so the pages are checked for their
// structure, not for particular programs or orders.
skipWithoutTestUser()

test('the account menu opens each account page', async ({ page }, testInfo) => {
  desktopOnly(testInfo.project.name)
  await signIn(page)

  const pages = [
    { link: 'My programs', url: /\/me$/, heading: 'My programs' },
    { link: 'Orders', url: /\/me\/orders$/, heading: 'Orders' },
    { link: 'Account settings', url: /\/me\/account$/, heading: 'Account settings' },
  ]
  for (const p of pages) {
    await page.getByRole('button', { name: 'Account' }).click()
    await page.locator('#account-menu').getByRole('link', { name: p.link, exact: true }).click()
    await expect(page).toHaveURL(p.url)
    await expect(page.getByRole('heading', { level: 2, name: p.heading })).toBeVisible()
  }

  // The tabs reach the workspaces page too; it lists workspaces or offers to open one.
  await page.getByRole('navigation', { name: 'My account' }).getByRole('link', { name: 'Workspaces' }).click()
  await expect(page).toHaveURL(/\/me\/workspaces$/)
  await expect(page.getByRole('heading', { level: 2, name: 'Workspaces' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Open a workspace' })).toBeVisible()
})
