import { defineConfig, devices } from '@playwright/test'

// End-to-end tests against a deployed environment (design §15.1): staging after every deploy, or any
// environment with E2E_BASE_URL. The shop is checked at three sizes, phone to desktop (design §8).
// Locally, E2E_CHANNEL=chrome uses the installed Chrome instead of downloading Playwright's Chromium.
const baseURL = process.env.E2E_BASE_URL || 'https://ca-athlos-gateway-staging.ashybush-033a4967.italynorth.azurecontainerapps.io'
// Unset GitHub variables arrive as empty strings, hence || rather than ??.
const channel = process.env.E2E_CHANNEL || undefined

export default defineConfig({
  testDir: './specs',
  globalSetup: './warm-up.ts',
  // The services scale to zero; a first request can take a minute and a half (design §15.3).
  timeout: 180_000,
  expect: { timeout: 30_000 },
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL,
    channel,
    navigationTimeout: 120_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], channel } },
    { name: 'tablet', use: { ...devices['Desktop Chrome'], channel, viewport: { width: 820, height: 1180 }, hasTouch: true } },
    { name: 'phone', use: { ...devices['Pixel 7'], channel } },
  ],
})
