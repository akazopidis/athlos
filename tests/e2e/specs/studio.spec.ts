import { expect, test, type Page } from '@playwright/test'
import { desktopOnly, signIn, skipWithoutTestUser } from '../signIn'

// A coach's studio from start to finish (design §3.4, §8), as the test customer: open a throwaway
// workspace, create a program (the €10 minimum holds), add a workout, see publishing refused without
// payouts (the test customer has no Stripe account), see themselves as the team's owner, add and remove a
// discount code, find no reviews yet, and delete the workspace, which has no sales, so nothing holds it.
// The workspace is deleted even when a step fails; its address (e2e-…) stays taken, like every deleted
// workspace's.
skipWithoutTestUser()

test('a coach opens a workspace, creates a program, and deletes the workspace', async ({ page }, testInfo) => {
  desktopOnly(testInfo.project.name)
  const slug = `e2e-${Date.now().toString(36)}`
  await signIn(page)

  await page.goto('/studio/new')
  await page.getByLabel('Name').fill('E2E Studio')
  await page.getByLabel('Address').fill(slug)
  await page.getByRole('checkbox', { name: /I accept the Terms for coaches/ }).check()
  await page.getByRole('button', { name: 'Create workspace' }).click()
  await page.waitForURL(new RegExp(`/studio/${slug}$`))

  try {
    // The minimum price: the form refuses €9 before anything is sent.
    await page.getByLabel('Title').fill('E2E Program')
    await page.getByLabel('Address').fill('e2e-program')
    const price = page.getByLabel('Price (EUR)')
    await price.fill('9')
    await page.getByRole('button', { name: 'Create program' }).click()
    expect(await price.evaluate((input: HTMLInputElement) => input.validity.rangeUnderflow)).toBe(true)
    await expect(page).toHaveURL(new RegExp(`/studio/${slug}$`))

    await price.fill('19')
    await page.getByRole('button', { name: 'Create program' }).click()
    await page.waitForURL(new RegExp(`/studio/${slug}/programs/`))

    await page.getByRole('button', { name: 'Add workout' }).click()
    await page.locator('#new-title').fill('Day one')
    await page.getByRole('button', { name: 'Save workout' }).click()
    await expect(page.getByText('Day one')).toBeVisible()

    // Without finished Stripe onboarding a program cannot go on sale.
    await page.getByRole('button', { name: 'Publish', exact: true }).click()
    await expect(page.getByText('Finish setting up payouts before publishing.')).toBeVisible()

    await page.goto(`/studio/${slug}/settings`)
    const team = page.getByRole('region', { name: 'Team' }).or(page.locator('section', { has: page.getByRole('heading', { name: 'Team' }) }))
    await expect(team.getByText('(you)')).toBeVisible()
    await expect(team.getByText('Owner', { exact: true })).toBeVisible()

    // Discount codes (design §2.4): one is created for the workspace, listed, and deleted.
    await page.goto(`/studio/${slug}/discounts`)
    await page.getByLabel('Code', { exact: true }).fill('e2e-10')
    await page.getByLabel('Percent off').fill('10')
    await page.getByRole('button', { name: 'Create code' }).click()
    await expect(page.getByText('E2E-10', { exact: true })).toBeVisible()
    await page.getByRole('button', { name: 'Delete the code E2E-10' }).click()
    await expect(page.getByText('E2E-10', { exact: true })).toBeHidden()

    // Reviews arrive once customers buy; a new workspace has none.
    await page.goto(`/studio/${slug}/reviews`)
    await expect(page.getByText('No reviews yet')).toBeVisible()
  } finally {
    await deleteWorkspace(page, slug)
  }
})

async function deleteWorkspace(page: Page, slug: string) {
  await page.goto(`/studio/${slug}/settings`)
  await page.locator('#confirm-delete').fill(slug)
  await page.getByRole('button', { name: 'Delete workspace' }).click()
  await page.waitForURL(/\/me\/workspaces$/)
}
