import { act, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { SystemStatusDto } from '../api/types'

const getSystemStatus = vi.fn()
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  getSystemStatus: (...a: unknown[]) => getSystemStatus(...a),
}))

import { SystemStatusProvider, useSystemStatus } from './useSystemStatus'

const status = (over: Partial<SystemStatusDto> = {}): SystemStatusDto => ({
  vmSlot: { capacity: 1, busy: 0, holders: [] }, queueDepth: 0, runningTasks: 0, activeJobs: 0,
  disks: [{ name: 'data', path: '/var/lib/maldex', freeBytes: 50, totalBytes: 100 }],
  workers: [{ name: 'orchestrator', lastTickAt: '2026-10-25T10:00:00Z', expectedIntervalSec: 5, stalled: false }],
  recentFailures: [], generatedAt: '2026-10-25T10:00:00Z', ...over,
})

function Probe() {
  const { status: s, error, attention } = useSystemStatus()
  return <p>{s ? `queued ${s.queueDepth}` : 'none'}|{error ?? 'ok'}|{attention ? 'attn' : 'calm'}</p>
}

const setHidden = (hidden: boolean) => Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
const flush = () => act(async () => { await Promise.resolve(); await Promise.resolve() })
const tick = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms) })

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] })
  getSystemStatus.mockReset()
  getSystemStatus.mockResolvedValue(status())
  setHidden(false)
})
afterEach(() => { vi.useRealTimers(); setHidden(false) })

describe('SystemStatusProvider', () => {
  it('polls every 5 s only when enabled and pauses while hidden', async () => {
    const { rerender } = render(<SystemStatusProvider enabled={false}><Probe /></SystemStatusProvider>)
    await tick(20_000)
    expect(getSystemStatus).not.toHaveBeenCalled()

    rerender(<SystemStatusProvider enabled><Probe /></SystemStatusProvider>)
    await flush()
    expect(getSystemStatus).toHaveBeenCalledTimes(1)
    await tick(5000)
    expect(getSystemStatus).toHaveBeenCalledTimes(2)
    await tick(10_000)
    expect(getSystemStatus).toHaveBeenCalledTimes(4)

    setHidden(true)
    await tick(15_000)
    expect(getSystemStatus).toHaveBeenCalledTimes(4)

    setHidden(false)
    await act(async () => { document.dispatchEvent(new Event('visibilitychange')) })
    expect(getSystemStatus).toHaveBeenCalledTimes(5)

    rerender(<SystemStatusProvider enabled={false}><Probe /></SystemStatusProvider>)
    await tick(20_000)
    expect(getSystemStatus).toHaveBeenCalledTimes(5)
  })

  it('reports attention for a stalled worker and an error on failure', async () => {
    getSystemStatus.mockResolvedValueOnce(status({ workers: [{ name: 'w', lastTickAt: null, expectedIntervalSec: 5, stalled: true }] }))
    render(<SystemStatusProvider enabled><Probe /></SystemStatusProvider>)
    await flush()
    expect(screen.getByText(/attn/)).toBeInTheDocument()

    getSystemStatus.mockRejectedValueOnce(new Error('boom'))
    await tick(5000)
    expect(screen.getByText(/boom/)).toBeInTheDocument()
  })

  it('flags attention for a disk under 10% free but not at exactly 10%', async () => {
    getSystemStatus.mockResolvedValueOnce(status({ disks: [{ name: 'vm', path: '/v', freeBytes: 10, totalBytes: 100 }] }))
    render(<SystemStatusProvider enabled><Probe /></SystemStatusProvider>)
    await flush()
    expect(screen.getByText(/calm/)).toBeInTheDocument()

    getSystemStatus.mockResolvedValueOnce(status({ disks: [{ name: 'vm', path: '/v', freeBytes: 9, totalBytes: 100 }] }))
    await tick(5000)
    expect(screen.getByText(/attn/)).toBeInTheDocument()
  })
})
