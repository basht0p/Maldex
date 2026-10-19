import { defineConfig, devices } from '@playwright/test'

// The server lifecycle is owned by `./build.sh smoke` (database, hosted SPA build, backend on :5080, cleanup),
// so there is deliberately no `webServer` block here. Run via `./build.sh smoke`, not bare `npx playwright test`.
export default defineConfig({
  testDir: './e2e',
  timeout: 90_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:5080',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})
