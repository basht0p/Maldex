import { fireEvent, screen, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { SystemStatusDto } from '../api/types'

const getSystemStatus = vi.fn()
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  getSystemStatus: (...a: unknown[]) => getSystemStatus(...a),
}))

import { formatDuration } from '../kit/format'
import { SystemStatusProvider } from '../live/SystemStatusProvider'
import { renderWithKit } from '../test/renderWithKit'
import StatusPage from './StatusPage'

const GB = 1024 ** 3
const dto = (over: Partial<SystemStatusDto> = {}): SystemStatusDto => ({
  vmSlot: { capacity: 2, busy: 2, holders: [
    { kind: 'task', id: 't1', label: 'evil‮txt.exe', since: '2026-10-25T09:00:00Z' },
    { kind: 'job', id: 'j1', label: 'Refresh Golden', since: '2026-10-25T09:30:00Z' },
  ] },
  queueDepth: 3, runningTasks: 1, activeJobs: 1,
  disks: [
    { name: 'data', path: '/var/lib/maldex', freeBytes: 50 * GB, totalBytes: 100 * GB },
    { name: 'vm', path: '/var/lib/vm', freeBytes: 9 * GB, totalBytes: 100 * GB },
    { name: 'media', path: '/srv/media', freeBytes: 4 * GB, totalBytes: 100 * GB },
  ],
  workers: [
    { name: 'orchestrator', lastTickAt: '2026-10-25T10:00:00Z', expectedIntervalSec: 5, stalled: false },
    { name: 'retention', lastTickAt: null, expectedIntervalSec: 3600, stalled: true },
  ],
  recentFailures: [{ kind: 'task', id: 't9', label: '<img src=x onerror=alert(1)>', reason: 'bad‮reason', at: '2026-10-25T08:00:00Z' }],
  generatedAt: '2026-10-25T10:00:05Z', ...over,
})

const page = () => renderWithKit(<SystemStatusProvider enabled><StatusPage /></SystemStatusProvider>, { route: '/admin/status' })

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] })
  getSystemStatus.mockReset()
  getSystemStatus.mockResolvedValue(dto())
})
afterEach(() => vi.useRealTimers())

describe('StatusPage', () => {
  it('renders slot holders, counts, disks with thresholds, workers and failures', async () => {
    page()
    expect(await screen.findByText('2 of 2 in use')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'evil\\u202Etxt.exe' })).toHaveAttribute('href', '/tasks/t1')
    expect(screen.getByRole('link', { name: 'Refresh Golden' })).toHaveAttribute('href', '/admin/template-jobs/j1')
    expect(screen.getByText('Queued').nextSibling).toHaveTextContent('3')
    expect(screen.getByText('Running').nextSibling).toHaveTextContent('1')
    expect(screen.getByText('Active jobs').nextSibling).toHaveTextContent('1')

    expect(screen.getByText('/var/lib/maldex').closest('[data-level]')).toHaveAttribute('data-level', 'ok')
    expect(screen.getByText('/var/lib/vm').closest('[data-level]')).toHaveAttribute('data-level', 'amber')
    expect(screen.getByText('/srv/media').closest('[data-level]')).toHaveAttribute('data-level', 'red')
    expect(screen.getByText(/50\.0 GiB free of 100\.0 GiB/)).toBeInTheDocument()

    const stalled = screen.getByRole('row', { name: /retention/ })
    expect(within(stalled).getByText('Stalled')).toBeInTheDocument()
    expect(within(stalled).getByText('Not yet')).toBeInTheDocument()
    expect(within(screen.getByRole('row', { name: /orchestrator/ })).queryByText('Stalled')).toBeNull()

    expect(screen.getByRole('link', { name: '<img src=x onerror=alert(1)>' })).toHaveAttribute('href', '/tasks/t9')
    expect(document.querySelector('img[onerror]')).toBeNull()
    expect(screen.getByText('bad\\u202Ereason')).toBeInTheDocument()
    expect(screen.getByText(/Last updated/)).toBeInTheDocument()
  })

  it('shows the expected interval as a duration and an unknown disk size without a bar', async () => {
    getSystemStatus.mockResolvedValue(dto({ disks: [{ name: 'data', path: '/odd', freeBytes: 0, totalBytes: 0 }] }))
    page()
    await screen.findByText('2 of 2 in use')
    expect(screen.queryByText('3600 s')).toBeNull()
    expect(within(screen.getByRole('row', { name: /retention/ })).getByText(formatDuration(3600))).toBeInTheDocument()
    expect(screen.getByText('Unknown size')).toBeInTheDocument()
    expect(screen.getByText('/odd').closest('[data-level]')).toHaveAttribute('data-level', 'ok')
    expect(screen.queryByRole('meter')).toBeNull()
    expect(screen.queryByText('Critically low')).toBeNull()
  })

  it('shows an ErrorBanner with Retry when status fails', async () => {
    getSystemStatus.mockRejectedValueOnce(new Error('status down'))
    page()
    expect(await screen.findByText('status down')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await screen.findByText('2 of 2 in use')).toBeInTheDocument()
    expect(screen.queryByText('status down')).toBeNull()
  })
})
