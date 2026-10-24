// A returning visitor: opens the page, then opens it again in the same browser, and counts how many of
// the second visit's scripts, styles and fonts came from the browser's cache without asking, how many
// were revalidated (304) and how many were downloaded again (Resource Timing).
//   node tests/evaluation/repeat_visit.mjs <page URL> [samples]
// Uses tests/e2e's Playwright (cd tests/e2e && npm ci) and the installed Chrome.
import { createRequire } from 'node:module'

const { chromium } = createRequire(new URL('../e2e/package.json', import.meta.url))('@playwright/test')
const url = process.argv[2]
const samples = Number(process.argv[3] ?? 3)

console.log('sample,assets,from_cache,revalidated,downloaded,second_visit_load_ms')
for (let sample = 1; sample <= samples; sample++) {
  const browser = await chromium.launch({ channel: 'chrome' })
  const page = await browser.newPage()
  await page.goto(url, { waitUntil: 'load' })
  await page.waitForTimeout(3000)

  const start = Date.now()
  await page.goto(url, { waitUntil: 'load' })
  const loadMs = Date.now() - start
  await page.waitForTimeout(3000)
  const assets = await page.evaluate(() =>
    performance
      .getEntriesByType('resource')
      .filter((e) => /\.(js|css|woff2)$/.test(new URL(e.name).pathname))
      .map((e) => ({ status: e.responseStatus, transfer: e.transferSize, body: e.encodedBodySize })),
  )
  const fromCache = assets.filter((a) => a.transfer === 0).length
  // Chrome reports a revalidation (a 304 answer) as the cached 200, 300 bytes transferred and no body.
  const revalidated = assets.filter((a) => a.transfer > 0 && a.body === 0).length
  console.log([sample, assets.length, fromCache, revalidated, assets.length - fromCache - revalidated, loadMs].join(','))
  await browser.close()
}
