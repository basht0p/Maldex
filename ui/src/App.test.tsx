import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, beforeEach, expect, it, vi } from 'vitest'
import App from './App'

const me = (role: 'Admin' | 'Analyst') => http.get('/api/me', () => HttpResponse.json({ id: 'u1', displayName: 'Pat', role }))

const server = setupServer(
  http.get('/api/tasks', () => HttpResponse.json({ items: [], total: 0 })),
  http.get('/api/orgs', () => HttpResponse.json([])),
)
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
beforeEach(() => window.history.pushState({}, '', '/'))
afterEach(() => { server.resetHandlers(); vi.unstubAllGlobals() })
afterAll(() => server.close())

it('shows the Admin menu to admins only', async () => {
  const user = userEvent.setup()
  server.use(me('Admin'))
  const { unmount } = render(<App />)
  await user.click(await screen.findByRole('button', { name: 'Admin' }))
  for (const name of ['Templates', 'Media', 'Organizations', 'Status']) {
    expect(screen.getByRole('link', { name })).toBeInTheDocument()
  }
  expect(screen.getByRole('link', { name: 'Media' })).toHaveAttribute('href', '/admin/media')
  unmount()

  server.use(me('Analyst'))
  render(<App />)
  expect(await screen.findByText('Pat (Analyst)')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Admin' })).not.toBeInTheDocument()
})

it('opens shortcut help on ? and lists the six shortcuts', async () => {
  const user = userEvent.setup()
  server.use(me('Analyst'))
  render(<App />)
  await screen.findByText('Pat (Analyst)')
  await user.keyboard('?')
  const dialog = screen.getByRole('dialog', { name: 'Keyboard shortcuts' })
  expect(within(dialog).getAllByRole('listitem')).toHaveLength(6)
  await user.keyboard('{Escape}')
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
})

it('g s navigates to Submit, and g a only works for admins', async () => {
  const user = userEvent.setup()
  server.use(me('Analyst'))
  render(<App />)
  await screen.findByText('Pat (Analyst)')
  await user.keyboard('ga')
  expect(window.location.pathname).toBe('/')
  await user.keyboard('gs')
  expect(window.location.pathname).toBe('/submit')
})

it('collapses the nav into a menu button under 768 px', async () => {
  vi.stubGlobal('matchMedia', (q: string) => ({
    matches: q === '(max-width: 767px)', media: q, addEventListener: () => {}, removeEventListener: () => {},
  }))
  server.use(me('Analyst'))
  render(<App />)
  expect(await screen.findByRole('button', { name: 'Menu' })).toBeVisible()
  expect(screen.queryByRole('link', { name: 'Submit' })).not.toBeInTheDocument()
  await userEvent.setup().click(screen.getByRole('button', { name: 'Menu' }))
  expect(screen.getByRole('link', { name: 'Submit' })).toBeInTheDocument()
})

const statusBody = (stalled: boolean) => ({
  vmSlot: { capacity: 1, busy: 0, holders: [] }, queueDepth: 0, runningTasks: 0, activeJobs: 0,
  disks: [{ name: 'data', path: '/d', freeBytes: 50, totalBytes: 100 }],
  workers: [{ name: 'orchestrator', lastTickAt: null, expectedIntervalSec: 5, stalled }],
  recentFailures: [], generatedAt: '2026-10-25T10:00:00Z',
})

it('shows the attention dot on the Status link when a worker is stalled', async () => {
  server.use(me('Admin'), http.get('/api/admin/status', () => HttpResponse.json(statusBody(true))))
  window.history.pushState({}, '', '/admin/status')
  render(<App />)
  await screen.findByRole('heading', { name: 'System status' })
  await userEvent.setup().click(await screen.findByRole('button', { name: 'Admin' }))
  expect(within(screen.getByRole('link', { name: /Status/ })).getByRole('img', { name: 'needs attention' })).toBeInTheDocument()
})

it('does not request status for analysts or on non-admin pages', async () => {
  let calls = 0
  const status = http.get('/api/admin/status', () => { calls++; return HttpResponse.json(statusBody(false)) })
  server.use(me('Analyst'), status)
  window.history.pushState({}, '', '/admin/status')
  const { unmount } = render(<App />)
  await screen.findByText('Pat (Analyst)')
  unmount()
  server.use(me('Admin'), status)
  window.history.pushState({}, '', '/')
  render(<App />)
  await screen.findByText('Pat (Admin)')
  expect(calls).toBe(0)
})
