import { expect, test } from '@playwright/test'

// Drives the hosted SPA (same origin, fake backend at Speed=10): submit -> live telemetry -> verdict -> report.
test('submit a URL, watch live telemetry, stop, and read the report', async ({ page }) => {
  await page.goto('/')
  await page.getByRole('link', { name: 'Submit', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Submit a sample' })).toBeVisible()

  await page.getByLabel('Organization').selectOption({ label: 'Dev Org' })
  await page.getByLabel('URL').fill('https://example.com')
  await page.getByLabel('Network mode').selectOption('Simulated')
  await page.getByRole('button', { name: 'Submit', exact: true }).click()

  await expect(page).toHaveURL(/\/tasks\/[0-9a-f-]{36}$/)
  await expect(page.getByRole('heading', { name: 'Task', exact: true })).toBeVisible()

  // Live telemetry: the replay spawns notepad.exe.
  await expect(page.locator('ul').filter({ hasText: 'notepad.exe' }).first()).toBeVisible({ timeout: 15_000 })

  await page.getByRole('button', { name: 'Stop' }).click()
  await expect(page.getByText('Completed', { exact: true })).toBeVisible({ timeout: 30_000 })

  const popupPromise = page.waitForEvent('popup')
  await page.getByRole('link', { name: 'HTML report' }).click()
  const report = await popupPromise
  await expect(report.locator('body')).toContainText('No threats detected')
})
