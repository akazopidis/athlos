import { readFile } from 'node:fs/promises'
import { expect, test, type Page } from '@playwright/test'
import { expectAccessible } from '../axe'
import { desktopOnly, signIn, skipWithoutTestUser } from '../signIn'

// A coach's studio from start to finish (design §3.4, §8), as the test customer: open a throwaway
// workspace, create a program (the €10 minimum holds), add a workout, see publishing refused without
// payouts (the test customer has no Stripe account), see themselves as the team's owner, invite staff and
// revoke the invite, download the workspace's data, add and remove a discount code, find no reviews yet,
// and delete the workspace, which has no sales, so nothing holds it. A workspace accepts the Terms for
// coaches in force when it opens, so the studio shows no notice to accept new ones (design §5.4). Each
// studio page is checked for accessibility on the way (design §8).
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
    await expect(page.getByLabel('Title')).toBeVisible()
    await expect(page.getByText('New Terms for coaches')).toBeHidden()
    await expectAccessible(page, testInfo, 'studio overview')

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
    await expectAccessible(page, testInfo, 'program editor')

    // Without finished Stripe onboarding a program cannot go on sale.
    await page.getByRole('button', { name: 'Publish', exact: true }).click()
    await expect(page.getByText('Finish setting up payouts before publishing.')).toBeVisible()

    await page.goto(`/studio/${slug}/settings`)
    const team = page.getByRole('region', { name: 'Team' }).or(page.locator('section', { has: page.getByRole('heading', { name: 'Team' }) }))
    await expect(team.getByText('(you)')).toBeVisible()
    await expect(team.getByText('Owner', { exact: true })).toBeVisible()
    await expectAccessible(page, testInfo, 'studio settings')

    // Staff invites (design §5.2): the invite is listed until it is revoked. The address is at
    // example.com, reserved for examples, so the invite email reaches nobody; its bounce is recorded.
    const invitee = `${slug}@example.com`
    await page.getByLabel('Invite staff by email').fill(invitee)
    await page.getByRole('button', { name: 'Send invite' }).click()
    await expect(page.getByText('Invite sent.')).toBeVisible()
    const invite = page.getByRole('listitem').filter({ hasText: invitee })
    await expect(invite).toBeVisible()
    await invite.getByRole('button', { name: 'Revoke' }).click()
    await expect(invite).toBeHidden()

    // The owner's export (design §5.4): asked for, prepared after the request, then one JSON file with
    // the workspace and its program.
    await page.getByRole('button', { name: 'Prepare workspace data' }).click()
    const ready = page.getByRole('listitem').filter({ hasText: `Workspace data (${slug})` }).first()
    await expect(ready.getByRole('button', { name: 'Download' })).toBeVisible({ timeout: 90_000 })
    const download = page.waitForEvent('download')
    await ready.getByRole('button', { name: 'Download' }).click()
    const file = await download
    expect(file.suggestedFilename()).toMatch(new RegExp(`^athlos-${slug}-\\d{4}-\\d{2}-\\d{2}\\.json$`))
    const exported = JSON.parse(await readFile((await file.path())!, 'utf8'))
    expect(exported.workspace.slug).toBe(slug)
    expect(JSON.stringify(exported.programs)).toContain('E2E Program')

    // Discount codes (design §2.4): one is created for the workspace, listed, and deleted.
    await page.goto(`/studio/${slug}/discounts`)
    await expectAccessible(page, testInfo, 'discount codes')
    await page.getByLabel('Code', { exact: true }).fill('e2e-10')
    await page.getByLabel('Percent off').fill('10')
    await page.getByRole('button', { name: 'Create code' }).click()
    await expect(page.getByText('E2E-10', { exact: true })).toBeVisible()
    await page.getByRole('button', { name: 'Delete the code E2E-10' }).click()
    await expect(page.getByText('E2E-10', { exact: true })).toBeHidden()

    // Reviews arrive once customers buy; a new workspace has none.
    await page.goto(`/studio/${slug}/reviews`)
    await expect(page.getByText('No reviews yet')).toBeVisible()
    await expectAccessible(page, testInfo, 'studio reviews')

    await page.goto(`/studio/${slug}/sales`)
    await expectAccessible(page, testInfo, 'studio sales')
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
