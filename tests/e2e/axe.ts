import AxeBuilder from '@axe-core/playwright'
import { expect, type Page, type TestInfo } from '@playwright/test'

// Accessibility (design §8): axe checks the page against WCAG 2.1 A and AA once it has loaded; a
// serious or critical violation fails the test, and the full list is attached to the report.
export async function expectAccessible(page: Page, testInfo: TestInfo, name: string) {
  await expect(page.locator('main')).toBeVisible()
  // Lists keep showing earlier results while they load (aria-busy); wait until they are done.
  await expect(page.locator('[aria-busy="true"]')).toHaveCount(0)

  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
  await testInfo.attach(`axe ${name}`, { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' })
  const serious = results.violations
    .filter((v) => v.impact === 'serious' || v.impact === 'critical')
    .map((v) => `${name}: ${v.id} (${v.impact}): ${v.help} — ${v.nodes.length} element(s), e.g. ${v.nodes[0]?.target.join(' ')}`)
  expect.soft(serious).toEqual([])
}
