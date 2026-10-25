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
  await page.getByRole('dialog').getByRole('button', { name: 'Stop task' }).click()
  await expect(page.getByText('Completed', { exact: true }).first()).toBeVisible({ timeout: 30_000 })

  // The in-app report: tabbed, Overview first.
  await expect(page.getByRole('tab', { name: /Overview/ })).toHaveAttribute('aria-selected', 'true', { timeout: 30_000 })
  await expect(page.getByText('No threats detected').first()).toBeVisible()
  await page.getByRole('tab', { name: /Screenshots/ }).click()
  // The fake replay may or may not produce screenshots: accept an image or the empty state.
  await expect(page.locator('img[alt^="Screenshot at"]').first().or(page.getByText('No screenshots').first())).toBeVisible()
  await page.getByRole('tab', { name: /Events/ }).click()
  await expect(page.getByRole('button', { name: 'Export CSV' })).toBeVisible()

  // Reports menu -> HTML report popup.
  await page.getByRole('button', { name: 'Reports' }).click()
  const popupPromise = page.waitForEvent('popup')
  await page.getByRole('link', { name: 'HTML report' }).click()
  const report = await popupPromise
  await expect(report.locator('body')).toContainText('No threats detected')
  await report.close()

  // Task list search finds the submitted URL.
  await page.goto('/')
  await page.getByLabel('Search tasks').fill('https://example.com')
  await expect(page.getByRole('link', { name: /example\.com/ }).first()).toBeVisible()

  // Theme switch.
  await page.getByLabel('Theme').selectOption({ label: 'Dark' })
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')

  // Admin pages: Status, Templates -> detail, Media.
  await page.getByRole('button', { name: 'Admin' }).click()
  await page.getByRole('link', { name: 'Status' }).click()
  await expect(page.getByRole('heading', { name: 'System status' })).toBeVisible()
  await expect(page.getByText(/of \d+ in use/)).toBeVisible()
  await expect(page.getByRole('cell', { name: 'orchestrator', exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Admin' }).click()
  await page.getByRole('link', { name: 'Templates' }).click()
  await expect(page.getByRole('heading', { name: 'Sandbox Templates' })).toBeVisible()
  await page.getByRole('link', { name: 'Details' }).first().click()
  await expect(page).toHaveURL(/\/admin\/templates\/[\w-]+$/)
  await expect(page.locator('#template-title')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Versions' })).toBeVisible()

  await page.getByRole('button', { name: 'Admin' }).click()
  await page.getByRole('link', { name: 'Media' }).click()
  await expect(page.getByRole('heading', { name: 'Media' })).toBeVisible()
})
