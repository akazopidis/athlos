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
    { link: 'Saved', url: /\/me\/saved$/, heading: 'Saved programs' },
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

// Saved programs (design §2.4): the heart on a program's page keeps it under "Saved", and takes it
// out again. Any program on sale will do, so the shop's first one is used; a run that stopped halfway
// may have left it saved, so the test starts from "not saved".
test('a customer saves a program for later and removes it', async ({ page }, testInfo) => {
  desktopOnly(testInfo.project.name)
  await signIn(page)

  await page.goto('/')
  const first = page.locator('main article h3 a').first()
  const title = (await first.innerText()).trim()
  await first.click()
  await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()

  const unsave = page.getByRole('button', { name: `Remove ${title} from saved` })
  const save = page.getByRole('button', { name: `Save ${title} for later` })
  await expect(unsave.or(save)).toBeVisible()
  if (await unsave.isVisible()) {
    await unsave.click()
    await expect(save).toBeVisible()
  }
  await save.click()
  await expect(unsave).toBeVisible()

  await page.goto('/me/saved')
  const saved = page.locator('main article').filter({ hasText: title })
  await expect(saved).toBeVisible()
  await saved.getByRole('button', { name: `Remove ${title} from saved` }).click()
  await expect(saved).toHaveCount(0)
})
