import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, beforeEach, expect, it, vi } from 'vitest'
import App from './App'

const me = (role: 'Admin' | 'Analyst' | null) => http.get('/api/me', () => HttpResponse.json({ id: 'u1', displayName: 'Pat', role, kind: 'Local', totpEnabled: false, isBreakGlass: false, roleManagedByIdp: false }))
const authConfig = http.get('/api/auth/config', () => HttpResponse.json({ passwordLogin: true, sso: null }))

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

const statusBody = (over: Record<string, unknown> = {}) => ({
  vmSlot: { capacity: 1, busy: 0, holders: [] }, queueDepth: 0, runningTasks: 0, activeJobs: 0,
  disks: [{ name: 'data', path: '/d', freeBytes: 50, totalBytes: 100 }],
  workers: [{ name: 'orchestrator', lastTickAt: null, expectedIntervalSec: 5, stalled: false }],
  recentFailures: [], generatedAt: '2026-10-25T10:00:00Z', ...over,
})
const stalledWorkers = [{ name: 'orchestrator', lastTickAt: null, expectedIntervalSec: 5, stalled: true }]
const lowDisk = [{ name: 'vm', path: '/v', freeBytes: 5, totalBytes: 100 }]

it.each([
  ['a stalled worker', { workers: stalledWorkers }],
  ['a low disk', { disks: lowDisk }],
])('shows the attention dot on the Admin button and the Status link for %s', async (_n, over) => {
  server.use(me('Admin'), http.get('/api/admin/status', () => HttpResponse.json(statusBody(over))))
  window.history.pushState({}, '', '/admin/status')
  render(<App />)
  await screen.findByRole('heading', { name: 'System status' })
  const button = await screen.findByRole('button', { name: /^Admin/ })
  expect(await within(button).findByRole('img', { name: 'needs attention' })).toBeInTheDocument()
  await userEvent.setup().click(button)
  expect(within(screen.getByRole('link', { name: /Status/ })).getByRole('img', { name: 'needs attention' })).toBeInTheDocument()
})

it('does not request status for analysts or for an admin outside /admin', async () => {
  let calls = 0
  const status = http.get('/api/admin/status', () => { calls++; return HttpResponse.json(statusBody()) })
  const settle = () => new Promise((r) => setTimeout(r, 150))
  server.use(me('Analyst'), status)
  window.history.pushState({}, '', '/admin/status')
  const { unmount } = render(<App />)
  await screen.findByText('Pat (Analyst)')
  await settle()
  unmount()
  server.use(me('Admin'), status)
  window.history.pushState({}, '', '/')
  render(<App />)
  await screen.findByText('Pat (Admin)')
  await settle()
  expect(calls).toBe(0)
})

it('unauthenticated load routes to /login?returnUrl=<current path>', async () => {
  server.use(http.get('/api/me', () => HttpResponse.json({ error: 'unauthorized' }, { status: 401 })), authConfig)
  window.history.pushState({}, '', '/admin/orgs?tab=net')
  render(<App />)
  expect(await screen.findByLabelText('User name')).toBeInTheDocument()
  expect(window.location.pathname).toBe('/login')
  expect(new URLSearchParams(window.location.search).get('returnUrl')).toBe('/admin/orgs?tab=net')
  expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument()
})

it('role null routes to the waiting page with Sign out', async () => {
  let out = 0
  server.use(me(null), authConfig, http.post('/api/auth/logout', () => { out++; return new HttpResponse(null, { status: 204 }) }))
  render(<App />)
  expect(await screen.findByText('Your account has no Maldex role. Ask an administrator.')).toBeInTheDocument()
  expect(window.location.pathname).toBe('/waiting')
  expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument()
  await userEvent.setup().click(screen.getByRole('button', { name: 'Sign out' }))
  await waitFor(() => expect(window.location.pathname).toBe('/login'))
  expect(out).toBe(1)
})

it('user menu shows Account and Sign out; Sign out calls logout and routes to /login', async () => {
  let out = 0
  const user = userEvent.setup()
  server.use(me('Analyst'), authConfig, http.post('/api/auth/logout', () => { out++; return new HttpResponse(null, { status: 204 }) }))
  render(<App />)
  await user.click(await screen.findByRole('button', { name: /Pat/ }))
  expect(screen.getByRole('link', { name: 'Account' })).toHaveAttribute('href', '/account')
  await user.click(screen.getByRole('button', { name: 'Sign out' }))
  await waitFor(() => expect(window.location.pathname).toBe('/login'))
  expect(out).toBe(1)
})

it('a 403 csrf response shows a blocked toast', async () => {
  server.use(me('Analyst'), http.get('/api/tasks', () => HttpResponse.json({ error: 'csrf' }, { status: 403 })))
  render(<App />)
  expect(await screen.findByText('Request blocked; reload the page.')).toBeInTheDocument()
})
